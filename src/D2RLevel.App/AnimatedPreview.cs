using System.IO;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using D2RLevel.Assets;
using D2RLevel.Core;

namespace D2RLevel.App;

/// <summary>
/// A skinned character preview. The rig is posed and the mesh re-skinned on the CPU each
/// frame, then handed to WPF as fresh position and normal collections -- mutating the
/// existing collections item by item would raise a change notification per vertex.
/// </summary>
internal sealed class AnimatedPreview : IDisposable
{
    private readonly ModelAsset asset;
    private readonly RigPose pose;
    private readonly MeshGeometry3D[] geometries;
    private readonly float[][] positions;
    private readonly float[][] normals;
    private bool running, disposed;
    private TimeSpan lastRender = TimeSpan.MinValue;

    public Model3DGroup Model { get; }
    public CharacterRig Rig { get; }
    public IReadOnlyList<CharacterAnimation> Animations { get; }
    public IReadOnlyList<string> UnresolvedBones { get; }
    public CharacterAnimation? Current { get; private set; }
    /// <summary>Playback position in seconds within the current animation.</summary>
    public double Time { get; private set; }
    public double Duration => Current?.Duration ?? 0;
    public double Speed { get; set; } = 1;
    public bool IsPlaying => running;
    /// <summary>Raised after each advanced frame so the timeline can follow.</summary>
    public event Action? Advanced;

    private AnimatedPreview(ModelAsset asset, CharacterRig rig, IReadOnlyList<CharacterAnimation> animations, Model3DGroup model, MeshGeometry3D[] geometries)
    {
        this.asset = asset; Rig = rig; Animations = animations; Model = model; this.geometries = geometries;
        pose = new RigPose(rig);
        positions = asset.Parts.Select(p => new float[p.Positions.Length]).ToArray();
        normals = asset.Parts.Select(p => new float[p.Normals.Length]).ToArray();
        UnresolvedBones = pose.UnresolvedBones(asset);
    }

    /// <summary>
    /// Builds a posable preview, or returns null when this model is not a skinned
    /// character with a rig and animation set beside it.
    /// </summary>
    public static AnimatedPreview? TryCreate(string modelPath, AssetResolver resolver, Func<string, bool, string>? resolve, out string? reason)
    {
        reason = null;
        CharacterAssetPaths? paths;
        try { paths = CharacterAssets.Find(modelPath, resolver); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { reason = ex.Message; return null; }
        if (paths is null) { reason = "No rig and animation set beside this model."; return null; }
        try
        {
            var asset = ModelReader.Load(resolve?.Invoke(modelPath, true) ?? resolver.ResolvePreviewModel(modelPath, 0));
            if (!asset.IsSkinned) { reason = "This mesh carries no skin weights."; return null; }
            var rig = CharacterAnimationReader.LoadRig(paths.RigPath);
            var animations = CharacterAnimationReader.LoadAnimations(paths.AnimationsPath, rig);
            if (animations.Count == 0) { reason = "The animation set is empty."; return null; }

            var model = new Model3DGroup();
            var geometries = new MeshGeometry3D[asset.Parts.Count];
            var textures = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < asset.Parts.Count; i++)
            {
                var part = asset.Parts[i];
                var geometry = new MeshGeometry3D
                {
                    Positions = Points(part.Positions),
                    Normals = Vectors(part.Normals),
                    TextureCoordinates = new PointCollection(Enumerable.Range(0, part.UVs.Length / 2)
                        .Select(v => new System.Windows.Point(part.UVs[v * 2], part.UVs[v * 2 + 1]))),
                    TriangleIndices = new Int32Collection(part.Indices),
                };
                var material = PreviewMaterials.Default;
                if (part.AlbedoPath is { } albedo)
                {
                    if (!textures.TryGetValue(albedo, out var cached))
                    {
                        cached = PreviewMaterials.Default;
                        try { cached = PreviewMaterials.FromTexture(resolve?.Invoke(albedo, false) ?? resolver.Resolve(albedo), 1024, out _); }
                        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException) { }
                        textures[albedo] = cached;
                    }
                    material = cached;
                }
                geometries[i] = geometry;
                model.Children.Add(new GeometryModel3D(geometry, material) { BackMaterial = material });
            }
            return new AnimatedPreview(asset, rig, animations, model, geometries);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException)
        { reason = ex.Message; return null; }
    }

    private static Point3DCollection Points(float[] values)
    {
        var result = new Point3DCollection(values.Length / 3);
        for (int i = 0; i < values.Length; i += 3) result.Add(new Point3D(values[i], values[i + 1], values[i + 2]));
        return result;
    }
    private static Vector3DCollection Vectors(float[] values)
    {
        var result = new Vector3DCollection(values.Length / 3);
        for (int i = 0; i < values.Length; i += 3) result.Add(new Vector3D(values[i], values[i + 1], values[i + 2]));
        return result;
    }

    /// <summary>Selects an animation by name, resetting playback to its start.</summary>
    public void Select(CharacterAnimation? animation)
    {
        Current = animation; Time = 0; Refresh();
    }

    /// <summary>Moves to a point in the current animation without changing play state.</summary>
    public void Seek(double seconds)
    {
        Time = Duration > 0 ? Math.Clamp(seconds, 0, Duration) : 0;
        Refresh();
    }

    public void Play()
    {
        if (running || disposed || Current is null) return;
        running = true; lastRender = TimeSpan.MinValue;
        CompositionTarget.Rendering += OnRendering;
    }

    public void Pause()
    {
        if (!running) return;
        running = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs args || Current is null) return;
        // WPF raises Rendering more than once per frame; ignore the repeats.
        if (args.RenderingTime == lastRender) return;
        double elapsed = lastRender == TimeSpan.MinValue ? 0 : (args.RenderingTime - lastRender).TotalSeconds;
        lastRender = args.RenderingTime;
        if (Duration > 0)
        {
            Time += elapsed * Speed;
            // Loop, tolerating a long stall without spinning through the whole clip.
            if (Time > Duration || Time < 0) Time -= Math.Floor(Time / Duration) * Duration;
        }
        Refresh();
        Advanced?.Invoke();
    }

    /// <summary>Re-skins the mesh at the current time.</summary>
    public void Refresh()
    {
        pose.Apply(Current, (float)Time);
        for (int i = 0; i < asset.Parts.Count; i++)
        {
            pose.Skin3(asset.Parts[i], positions[i], normals[i]);
            geometries[i].Positions = Points(positions[i]);
            geometries[i].Normals = Vectors(normals[i]);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        Pause(); disposed = true;
    }
}
