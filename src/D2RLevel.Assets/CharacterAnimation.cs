using LSLib.Granny.GR2;
using LSLib.Granny.Model;
using OpenTK.Mathematics;

namespace D2RLevel.Assets;

/// <summary>
/// One bone of a character rig, in the skeleton's own bind pose. The decomposed bind
/// values are kept because a track that animates only some components falls back to them.
/// </summary>
public sealed record RigBone(string Name, int ParentIndex, Matrix4 LocalBind, Matrix4 InverseWorldBind,
    Vector3 BindTranslation, Quaternion BindRotation, Matrix3 BindScale);

public sealed record CharacterRig(string Name, IReadOnlyList<RigBone> Bones)
{
    public IReadOnlyDictionary<string, int> IndexByName { get; } =
        Bones.Select((b, i) => (b.Name, i)).GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.First().i, StringComparer.Ordinal);
}

/// <summary>A bone's sampled motion. Times are ascending; each array is one key per time.</summary>
public sealed record BoneTrack(string BoneName, float[] Times, Vector3[] Translations, Quaternion[] Rotations, Matrix3[] Scales);

public sealed record CharacterAnimation(string Name, float Duration, IReadOnlyList<BoneTrack> Tracks);

/// <summary>
/// Reads D2R character rigs and their animation sets. Skeletons and animations are Granny
/// containers like the meshes, so they come through the same reader; the curves are
/// decompressed once here and sampled per frame afterwards.
/// </summary>
public static class CharacterAnimationReader
{
    private const long SizeLimit = 128 * 1024 * 1024;

    public static CharacterRig LoadRig(string path)
    {
        var root = Read(path);
        var skeleton = root.Skeletons?.FirstOrDefault(s => s.Bones is { Count: > 0 })
            ?? throw new InvalidDataException("No skeleton in " + Path.GetFileName(path));
        var bones = skeleton.Bones.Select((bone, index) =>
        {
            if (string.IsNullOrEmpty(bone.Name)) throw new InvalidDataException("Unnamed bone in " + Path.GetFileName(path));
            if (bone.ParentIndex >= index) throw new InvalidDataException("Bones are not in parent-first order in " + Path.GetFileName(path));
            var inverse = bone.InverseWorldTransform;
            if (inverse is not { Length: 16 }) throw new InvalidDataException($"Bone {bone.Name} has no inverse world transform.");
            return new RigBone(bone.Name, bone.ParentIndex, bone.Transform.ToMatrix4Composite(), new Matrix4(
                inverse[0], inverse[1], inverse[2], inverse[3],
                inverse[4], inverse[5], inverse[6], inverse[7],
                inverse[8], inverse[9], inverse[10], inverse[11],
                inverse[12], inverse[13], inverse[14], inverse[15]),
                // Transform initialises absent components to identity, unlike Keyframe.
                bone.Transform.Translation, bone.Transform.Rotation, bone.Transform.ScaleShear);
        }).ToArray();
        return new(skeleton.Name ?? Path.GetFileNameWithoutExtension(path), bones);
    }

    public static IReadOnlyList<CharacterAnimation> LoadAnimations(string path, CharacterRig rig)
    {
        ArgumentNullException.ThrowIfNull(rig);
        var root = Read(path);
        var result = new List<CharacterAnimation>();
        foreach (var animation in root.Animations ?? [])
        {
            var tracks = new List<BoneTrack>();
            foreach (var group in animation.TrackGroups ?? [])
                foreach (var track in group.TransformTracks ?? [])
                {
                    if (string.IsNullOrEmpty(track.Name)) continue;
                    var keyframes = track.ToKeyframes();
                    // Position, rotation and scale are separate curves that need not share
                    // key times, so a keyframe can carry only some components. Fill the
                    // gaps between neighbours before reading any values out.
                    keyframes.InterpolateFrames();
                    var frames = keyframes.Keyframes;
                    if (frames.Count == 0) continue;
                    var times = frames.Keys.ToArray();
                    // Whatever a track never animates stays at the bone's bind value:
                    // an absent component reads as zero, not as an identity.
                    var bone = rig.IndexByName.TryGetValue(track.Name, out int index) ? rig.Bones[index] : null;
                    tracks.Add(new(track.Name, times,
                        times.Select(t => frames[t].HasTranslation ? frames[t].Translation : bone?.BindTranslation ?? Vector3.Zero).ToArray(),
                        times.Select(t => frames[t].HasRotation ? frames[t].Rotation : bone?.BindRotation ?? Quaternion.Identity).ToArray(),
                        times.Select(t => frames[t].HasScaleShear ? frames[t].ScaleShear : bone?.BindScale ?? Matrix3.Identity).ToArray()));
                }
            if (tracks.Count == 0) continue;
            float duration = animation.Duration > 0 ? animation.Duration : tracks.Max(t => t.Times[^1]);
            result.Add(new(animation.Name ?? "animation " + result.Count, duration, tracks));
        }
        return result.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static Root Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Animation asset is not extracted.", path);
        if (info.Length > SizeLimit) throw new InvalidDataException("Animation asset exceeds the editor's 128 MiB limit.");
        using var stream = new MemoryStream(File.ReadAllBytes(path), false);
        using var reader = new GR2Reader(stream);
        var root = new Root();
        reader.Read(root);
        return root;
    }
}

/// <summary>
/// Evaluates a rig at a point in time. The result is one skinning matrix per bone, in the
/// row-vector convention Granny uses: a bound vertex is transformed by
/// inverse-world-bind followed by the posed world transform.
/// </summary>
public sealed class RigPose
{
    private readonly CharacterRig rig;
    private readonly Matrix4[] world;
    private readonly Matrix4[] skin;
    public IReadOnlyList<Matrix4> Skin => skin;

    public RigPose(CharacterRig rig)
    {
        this.rig = rig;
        world = new Matrix4[rig.Bones.Count];
        skin = new Matrix4[rig.Bones.Count];
        Apply(null, 0);
    }

    /// <summary>Poses the rig; a null animation restores the bind pose.</summary>
    public void Apply(CharacterAnimation? animation, float time)
    {
        if (animation is null)
        {
            // Mesh vertices are authored in bind pose, so the rest pose is the identity.
            // Recomposing it from the rig would introduce drift on the handful of rigs
            // whose stored inverse-bind disagrees with their local bind chain.
            for (int i = 0; i < skin.Length; i++) { skin[i] = Matrix4.Identity; world[i] = Matrix4.Identity; }
            return;
        }
        var locals = new Matrix4[rig.Bones.Count];
        for (int i = 0; i < locals.Length; i++) locals[i] = rig.Bones[i].LocalBind;
        foreach (var track in animation.Tracks)
            if (rig.IndexByName.TryGetValue(track.BoneName, out int bone))
                locals[bone] = Sample(track, time);
        for (int i = 0; i < locals.Length; i++)
        {
            int parent = rig.Bones[i].ParentIndex;
            // Granny composes a child onto its parent, not the other way round.
            world[i] = parent < 0 ? locals[i] : locals[i] * world[parent];
            skin[i] = rig.Bones[i].InverseWorldBind * world[i];
        }
    }

    private static Matrix4 Sample(BoneTrack track, float time)
    {
        var times = track.Times;
        int next = 0;
        while (next < times.Length && times[next] < time) next++;
        Vector3 translation; Quaternion rotation; Matrix3 scale;
        if (next == 0) { translation = track.Translations[0]; rotation = track.Rotations[0]; scale = track.Scales[0]; }
        else if (next >= times.Length)
        { translation = track.Translations[^1]; rotation = track.Rotations[^1]; scale = track.Scales[^1]; }
        else
        {
            int previous = next - 1;
            float span = times[next] - times[previous];
            float t = span > 0 ? Math.Clamp((time - times[previous]) / span, 0, 1) : 0;
            translation = Vector3.Lerp(track.Translations[previous], track.Translations[next], t);
            rotation = Quaternion.Slerp(track.Rotations[previous], track.Rotations[next], t);
            scale = t < .5f ? track.Scales[previous] : track.Scales[next];
        }
        // Compose through Granny's own transform so scale/rotation/translation order and
        // handedness match the rig's bind pose exactly, rather than a parallel convention.
        return new Transform
        {
            Flags = (uint)(Transform.TransformFlags.HasTranslation | Transform.TransformFlags.HasRotation | Transform.TransformFlags.HasScaleShear),
            Translation = translation,
            Rotation = rotation,
            ScaleShear = scale,
        }.ToMatrix4Composite();
    }

    /// <summary>
    /// Bone names an asset binds that this rig does not contain. Their influences are
    /// skipped when posing, so a caller can tell the user what will not animate.
    /// </summary>
    public IReadOnlyList<string> UnresolvedBones(ModelAsset asset) =>
        (asset.Parts ?? []).Select(p => p.Skin).OfType<MeshSkin>().SelectMany(s => s.BoneNames)
            .Where(name => !rig.IndexByName.ContainsKey(name))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Skins one mesh part into <paramref name="target"/>, which must hold three floats per
    /// vertex. Bone slots resolve through the part's own binding names.
    /// </summary>
    public void Skin3(MeshPart part, float[] target, float[]? normalTarget = null)
    {
        var mesh = part.Skin ?? throw new InvalidOperationException("This mesh part carries no skin weights.");
        if (target.Length != part.Positions.Length) throw new ArgumentException("Target buffer does not match the part.", nameof(target));
        var lookup = new int[mesh.BoneNames.Length];
        for (int i = 0; i < lookup.Length; i++)
            lookup[i] = rig.IndexByName.TryGetValue(mesh.BoneNames[i], out int bone) ? bone : -1;
        int vertices = part.Positions.Length / 3;
        for (int v = 0; v < vertices; v++)
        {
            float px = part.Positions[v * 3], py = part.Positions[v * 3 + 1], pz = part.Positions[v * 3 + 2];
            float nx = 0, ny = 0, nz = 0, ox = 0, oy = 0, oz = 0;
            if (normalTarget is not null)
            { nx = part.Normals[v * 3]; ny = part.Normals[v * 3 + 1]; nz = part.Normals[v * 3 + 2]; }
            float sx = 0, sy = 0, sz = 0, applied = 0;
            for (int k = 0; k < 4; k++)
            {
                float weight = mesh.Weights[v * 4 + k];
                if (weight <= 0) continue;
                int bone = lookup[mesh.Indices[v * 4 + k]];
                if (bone < 0) continue;
                applied += weight;
                var m = skin[bone];
                sx += weight * (px * m.M11 + py * m.M21 + pz * m.M31 + m.M41);
                sy += weight * (px * m.M12 + py * m.M22 + pz * m.M32 + m.M42);
                sz += weight * (px * m.M13 + py * m.M23 + pz * m.M33 + m.M43);
                if (normalTarget is null) continue;
                ox += weight * (nx * m.M11 + ny * m.M21 + nz * m.M31);
                oy += weight * (nx * m.M12 + ny * m.M22 + nz * m.M32);
                oz += weight * (nx * m.M13 + ny * m.M23 + nz * m.M33);
            }
            // A vertex bound only to bones this rig lacks keeps its bind position rather
            // than collapsing to the origin: some meshes reference bones outside the rig.
            if (applied <= 0)
            {
                target[v * 3] = px; target[v * 3 + 1] = py; target[v * 3 + 2] = pz;
                if (normalTarget is not null)
                { normalTarget[v * 3] = nx; normalTarget[v * 3 + 1] = ny; normalTarget[v * 3 + 2] = nz; }
                continue;
            }
            target[v * 3] = sx; target[v * 3 + 1] = sy; target[v * 3 + 2] = sz;
            if (normalTarget is null) continue;
            float length = MathF.Sqrt(ox * ox + oy * oy + oz * oz);
            if (length > 0) { ox /= length; oy /= length; oz /= length; }
            normalTarget[v * 3] = ox; normalTarget[v * 3 + 1] = oy; normalTarget[v * 3 + 2] = oz;
        }
    }
}
