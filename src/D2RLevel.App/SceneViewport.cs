using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using D2RLevel.Core;

namespace D2RLevel.App;

public sealed record SceneItem(PresetEntity Entity, Model3DGroup Geometry, bool IsPlaceholder, IReadOnlyList<string>? TexturePaths = null);
public sealed record LoadedScene(IReadOnlyList<SceneItem> Items, IReadOnlyList<string> Diagnostics, int LoadedModels, IReadOnlyList<SceneBatch>? Batches = null, int MissingDecoderModels = 0);

public sealed class SceneViewport : Grid
{
    // Input belongs to the surrounding Grid. Avoid WPF's automatic software ray
    // tests on pointer movement; explicit visual hit testing below still picks clicks.
    private readonly Viewport3D viewport = new() { IsHitTestVisible = false, ClipToBounds = false };
    private readonly PerspectiveCamera camera = new() { FieldOfView = 55, NearPlaneDistance = 0.001, FarPlaneDistance = 100000 };
    private readonly Dictionary<ModelVisual3D, PresetEntity> entities = new();
    private readonly Dictionary<int, ModelVisual3D> visuals = new();
    private readonly HashSet<int> placeholders = new();
    private readonly ModelVisual3D selection = new();
    private readonly Dictionary<GeometryModel3D, SceneBatch> batchHits = new();
    private readonly Dictionary<SceneBatch, ModelVisual3D> batchVisuals = new();
    private readonly Dictionary<int, List<SceneBatch>> entityBatches = new();
    private readonly Dictionary<SceneBatch, Rect3D> batchBounds = new();
    private Point3D target = new();
    private double yaw = Math.PI / 4, pitch = Math.Asin(0.5), distance = 300;
    private double travelStep = 10;
    private bool orbitGesture;
    private Point3D workCenter;
    internal Point3D CameraPosition => camera.Position;
    internal Vector3D ViewDirection => camera.LookDirection;
    private Point last;
    private PresetEntity? selected;
    private bool terrainVisible = true;
    public void SetTerrainVisible(bool visible)
    {
        CancelDrag(); terrainVisible = visible;
        foreach (var pair in entities.Where(p => p.Value.IsTerrain && !entityBatches.ContainsKey(p.Value.Index)))
        {
            if (visible && !viewport.Children.Contains(pair.Key)) viewport.Children.Add(pair.Key);
            if (!visible) viewport.Children.Remove(pair.Key);
        }
        UpdateVisibility(); Select(selected);
    }
    internal string VerifyTerrainVisibility()
    {
        int Count(bool terrain) => batchVisuals.Count(p => p.Key.IsTerrain == terrain && p.Value.Content is not null) +
            entities.Count(p => p.Value.IsTerrain == terrain && !entityBatches.ContainsKey(p.Value.Index) && viewport.Children.Contains(p.Key));
        SetTerrainVisible(true); int initial = Count(true), props = Count(false);
        if (initial == 0) throw new InvalidOperationException("No visible terrain in smoke scene.");
        SetTerrainVisible(false);
        if (Count(true) != 0 || Count(false) != props) throw new InvalidOperationException("Terrain toggle changed prop visibility or left terrain visible.");
        UpdateCamera();
        if (Count(true) != 0) throw new InvalidOperationException("Camera update restored hidden terrain.");
        SetTerrainVisible(true);
        if (Count(true) != initial) throw new InvalidOperationException("Terrain did not restore.");
        return "PASS terrain: hide/show isolates terrain, keeps props visible and survives camera visibility updates.";
    }
    private double? workRadius;
    private readonly TextBlock modeLabel = new() { Margin = new Thickness(12), VerticalAlignment = VerticalAlignment.Bottom, Foreground = Brushes.LightBlue, IsHitTestVisible = false };
    public event Action<PresetEntity>? EntitySelected;
    public event Action<PresetEntity, EntityTransform>? DragCommitted;
    public bool AllowModelDragging { get; set; }
    private PresetEntity? dragEntity;
    private EntityTransform dragStart, dragValue;
    private Point dragPointer;
    private Point3D dragAnchor;
    private bool dragging, detached;
    public Vector3d PlacementPosition => GroundPoint(new(ActualWidth / 2, ActualHeight / 2), 0) is { } p
        ? new(p.X, 0, p.Z) : new(target.X, 0, target.Z);

    internal SceneItem? GetItem(PresetEntity entity) => visuals.TryGetValue(entity.Index, out var visual) && visual.Content is Model3DGroup geometry
        ? new(entity, geometry, placeholders.Contains(entity.Index)) : null;
    public void AddItem(SceneItem item)
    {
        if (item.IsPlaceholder) placeholders.Add(item.Entity.Index);
        var visual = new ModelVisual3D { Content = item.Geometry, Transform = Transform(item.Entity.Transform) };
        visuals.Add(item.Entity.Index, visual); entities.Add(visual, item.Entity);
        if (entityBatches.TryGetValue(item.Entity.Index, out var batches))
        {
            foreach (var batch in batches) batch.SetIncluded(item.Entity, true);
            RebuildBatches(item.Entity); UpdateVisibility();
        }
        else viewport.Children.Add(visual);
    }
    public void RemoveItem(PresetEntity entity)
    {
        placeholders.Remove(entity.Index);
        CancelDrag();
        if (entityBatches.TryGetValue(entity.Index, out var batches))
        {
            foreach (var batch in batches) batch.SetIncluded(entity, false);
            RebuildBatches(entity); UpdateVisibility();
        }
        if (visuals.Remove(entity.Index, out var visual)) { viewport.Children.Remove(visual); entities.Remove(visual); }
        if (selected == entity) Select(null);
    }

    internal Point3D? GroundPoint(Point point, double height)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return null;
        var forward = camera.LookDirection; forward.Normalize();
        var right = Vector3D.CrossProduct(forward, camera.UpDirection); right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        double tangent = Math.Tan(camera.FieldOfView * Math.PI / 360);
        var ray = forward + right * ((2 * point.X / ActualWidth - 1) * tangent) +
            up * ((1 - 2 * point.Y / ActualHeight) * tangent * ActualHeight / ActualWidth);
        if (Math.Abs(ray.Y) < 0.0001) return null;
        double t = (height - camera.Position.Y) / ray.Y;
        if (!double.IsFinite(t) || t <= 0) return null;
        return camera.Position + ray * t;
    }

    internal bool BeginDrag(PresetEntity entity, Point point)
    {
        CancelDrag();
        if (!AllowModelDragging || !entity.CanTransform || entity.HasParent || !visuals.ContainsKey(entity.Index) ||
            GroundPoint(point, entity.Transform.Position.Y) is not { } anchor) return false;
        dragEntity = entity; dragStart = dragValue = entity.Transform; dragAnchor = anchor; dragPointer = point;
        return true;
    }
    internal void ContinueDrag(Point point)
    {
        if (dragEntity is not { } entity || GroundPoint(point, dragStart.Position.Y) is not { } ground) return;
        if (!dragging && Math.Abs(point.X - dragPointer.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - dragPointer.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (!dragging)
        {
            dragging = true;
            if (entityBatches.ContainsKey(entity.Index))
            {
                RebuildBatches(entity, entity); viewport.Children.Add(visuals[entity.Index]); detached = true;
            }
            Cursor = Cursors.SizeAll;
        }
        var delta = ground - dragAnchor;
        dragValue = dragStart with { Position = new(dragStart.Position.X + delta.X, dragStart.Position.Y, dragStart.Position.Z + delta.Z) };
        visuals[entity.Index].Transform = Transform(dragValue); Select(entity);
    }
    public void CancelDrag() => EndDrag(false);
    internal void EndDrag(bool commit)
    {
        var entity = dragEntity; var value = dragValue; bool changed = dragging && value != dragStart;
        bool restoreBatches = detached;
        dragEntity = null; dragging = detached = false; Cursor = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (entity is null) return;
        if (restoreBatches) viewport.Children.Remove(visuals[entity.Index]);
        try { if (commit && changed) DragCommitted?.Invoke(entity, value); }
        finally
        {
            visuals[entity.Index].Transform = Transform(entity.Transform);
            if (restoreBatches) RebuildBatches(entity);
            Select(entity); UpdateVisibility();
        }
    }
    private void RebuildBatches(PresetEntity entity, PresetEntity? exclude = null)
    {
        if (!entityBatches.TryGetValue(entity.Index, out var batches)) return;
        foreach (var batch in batches)
        {
            batchHits.Remove(batch.Model); batch.Rebuild(exclude); batchHits[batch.Model] = batch;
            batchVisuals[batch].Content = batch.Model; batchBounds[batch] = batch.Model.Bounds;
        }
    }

    public async Task<string> MeasureOrbit(double? radius = null)
    {
        if (radius is { } r) { distance = r; UpdateCamera(); }
        var samples = new List<double>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double previous = 0; TimeSpan lastRender = TimeSpan.MinValue;
        double originalYaw = yaw;
        void FrameRendered(object? sender, EventArgs args)
        {
            var timestamp = ((RenderingEventArgs)args).RenderingTime;
            if (timestamp == lastRender) return;
            lastRender = timestamp;
            double now = clock.Elapsed.TotalMilliseconds;
            if (previous != 0) samples.Add(now - previous);
            previous = now; yaw += 0.025; UpdateCamera();
            if (samples.Count >= 90) done.TrySetResult();
        }
        CompositionTarget.Rendering += FrameRendered;
        try { await Task.WhenAny(done.Task, Task.Delay(20000)); }
        finally { CompositionTarget.Rendering -= FrameRendered; yaw = originalYaw; UpdateCamera(); }
        if (samples.Count == 0) throw new InvalidOperationException("No render callbacks during orbit probe.");
        var ordered = samples.Order().ToArray();
        return $"Orbit render-callback intervals: {samples.Count} samples · median {ordered[ordered.Length / 2]:F1}ms · p95 {ordered[(int)((ordered.Length - 1) * 0.95)]:F1}ms (UI cadence, not GPU FPS)";
    }

    public SceneViewport()
    {
        Background = new SolidColorBrush(Color.FromRgb(19, 26, 35));
        ClipToBounds = true;
        Focusable = true;
        Children.Add(viewport);
        Children.Add(modeLabel);
        viewport.Camera = camera;
        MouseDown += Down;
        MouseMove += Move;
        MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Left && dragEntity is not null) { EndDrag(true); e.Handled = true; } else if (e.ChangedButton is MouseButton.Right or MouseButton.Middle) ReleaseMouseCapture(); };
        LostMouseCapture += (_, _) => { orbitGesture = false; CancelDrag(); };
        LostKeyboardFocus += (_, _) => { if (IsMouseCaptured) ReleaseMouseCapture(); };
        Unloaded += (_, _) => { if (IsMouseCaptured) ReleaseMouseCapture(); };
        MouseWheel += (_, e) => { if (dragEntity is null) Dolly(e.Delta / 120d, TravelMultiplier()); e.Handled = true; };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { CancelDrag(); e.Handled = true; } if (e.Key == Key.F) { CancelDrag(); FrameSelected(); e.Handled = true; } if (e.Key == Key.Home) { CancelDrag(); FrameAll(); e.Handled = true; } };
        UpdateCamera();
        SizeChanged += (_, _) => UpdateVisibility();
    }

    public void SetScene(LoadedScene scene)
    {
        CancelDrag();
        viewport.Children.Clear(); entities.Clear(); visuals.Clear(); placeholders.Clear(); selected = null;
        batchHits.Clear(); batchVisuals.Clear(); entityBatches.Clear(); batchBounds.Clear();
        var lighting = new Model3DGroup();
        lighting.Children.Add(new AmbientLight(Color.FromRgb(150, 150, 150)));
        lighting.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-0.5, -1, -0.3)));
        viewport.Children.Add(new ModelVisual3D { Content = lighting });
        foreach (var item in scene.Items)
        {
            if (item.IsPlaceholder) placeholders.Add(item.Entity.Index);
            var visual = new ModelVisual3D { Content = item.Geometry, Transform = Transform(item.Entity.Transform) };
            if (scene.Batches is null) viewport.Children.Add(visual);
            entities[visual] = item.Entity;
            visuals[item.Entity.Index] = visual;
        }
        if (scene.Batches is not null)
            foreach (var batch in scene.Batches)
            {
                var visual = new ModelVisual3D { Content = batch.Model };
                batchVisuals[batch] = visual; batchHits[batch.Model] = batch; viewport.Children.Add(visual);
                batchBounds[batch] = batch.Model.Bounds;
                foreach (var entity in batch.Parts.Select(p => p.Entity).Distinct())
                {
                    if (!entityBatches.TryGetValue(entity.Index, out var list)) entityBatches[entity.Index] = list = [];
                    list.Add(batch);
                }
            }
        selection.Content = null;
        viewport.Children.Add(selection);
        SetTerrainVisible(terrainVisible);
        FrameAll();
    }

    public static Transform3D Transform(EntityTransform t)
    {
        // Keep native X/Z so the default camera agrees with the DS1 isometric axes.
        var group = new Transform3DGroup();
        group.Children.Add(new ScaleTransform3D(t.Scale.X, t.Scale.Y, t.Scale.Z));
        group.Children.Add(new RotateTransform3D(new QuaternionRotation3D(new Quaternion(t.Orientation.X, t.Orientation.Y, t.Orientation.Z, t.Orientation.W))));
        group.Children.Add(new TranslateTransform3D(t.Position.X, t.Position.Y, t.Position.Z));
        group.Freeze();
        return group;
    }

    public void UpdateEntity(PresetEntity entity)
    {
        if (visuals.TryGetValue(entity.Index, out var visual)) visual.Transform = Transform(entity.Transform);
        if (entityBatches.TryGetValue(entity.Index, out var batches))
            foreach (var batch in batches)
            {
                batchHits.Remove(batch.Model); batch.Rebuild(); batchHits[batch.Model] = batch;
                batchVisuals[batch].Content = batch.Model;
                batchBounds[batch] = batch.Model.Bounds;
            }
        UpdateVisibility();
        Select(entity);
    }

    public void Select(PresetEntity? entity)
    {
        selected = entity;
        selection.Content = null;
        if (entity?.IsTerrain == true && !terrainVisible) return;
        if (entity is null || !visuals.TryGetValue(entity.Index, out var visual)) return;
        var bounds = visual.Transform.TransformBounds(visual.Content.Bounds);
        var outline = WireBox(bounds, Colors.Turquoise); outline.Freeze(); selection.Content = outline;
    }
    public void FrameSelected()
    {
        if (selected is not null && visuals.TryGetValue(selected.Index, out var visual))
            Frame(visual.Transform.TransformBounds(visual.Content.Bounds));
    }
    public ModelFootprint? GetFootprint(PresetEntity entity)
    {
        if (placeholders.Contains(entity.Index) || !visuals.TryGetValue(entity.Index, out var visual)) return null;
        var b = visual.Transform.TransformBounds(visual.Content.Bounds);
        if (b.IsEmpty) return null;
        return new(entity.Name, b.X, b.Z, b.X + b.SizeX, b.Z + b.SizeZ);
    }
    public void FrameAll()
    {
        workRadius = null; modeLabel.Text = "";
        yaw = Math.PI / 4; pitch = Math.Asin(0.5);
        var bounds = Rect3D.Empty;
        foreach (var v in visuals.Values) bounds.Union(v.Transform.TransformBounds(v.Content.Bounds));
        if (!bounds.IsEmpty) Frame(bounds);
    }
    public bool FocusArea()
    {
        if (batchVisuals.Count == 0) return false;
        FrameSelected(); workRadius = 80; distance = 160;
        travelStep = 4.8;
        workCenter = target;
        modeLabel.Text = "LOCAL EDITING AREA · about 80 units around focus · Home restores the whole map";
        UpdateCamera();
        return true;
    }
    private void Frame(Rect3D bounds)
    {
        target = new(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
        var radius = Math.Sqrt(bounds.SizeX * bounds.SizeX + bounds.SizeY * bounds.SizeY + bounds.SizeZ * bounds.SizeZ) / 2;
        var aspect = Math.Max(1, ActualWidth / Math.Max(1, ActualHeight));
        var halfAngle = Math.Atan(Math.Tan(camera.FieldOfView * Math.PI / 360) / aspect);
        distance = Math.Max(0.02, radius / Math.Sin(halfAngle) * 1.12);
        travelStep = Math.Clamp(radius * 0.06, 0.02, 20);
        if (workRadius is not null) workCenter = target;
        UpdateCamera();
    }
    private void UpdateCamera()
    {
        var offset = new Vector3D(Math.Cos(pitch) * Math.Sin(yaw), Math.Sin(pitch), Math.Cos(pitch) * Math.Cos(yaw)) * distance;
        camera.Position = target + offset; camera.LookDirection = -offset; camera.UpDirection = new(0, 1, 0);
        UpdateVisibility();
    }
    private void UpdateVisibility()
    {
        if (batchVisuals.Count == 0) return;
        var forward = camera.LookDirection; forward.Normalize();
        var right = Vector3D.CrossProduct(forward, camera.UpDirection); right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        double horizontal = Math.Tan(camera.FieldOfView * Math.PI / 360) * 1.1;
        double vertical = horizontal * Math.Max(1, ActualHeight) / Math.Max(1, ActualWidth);
        foreach (var (batch, visual) in batchVisuals)
        {
            var b = batchBounds[batch];
            if (b.IsEmpty) continue;
            var center = new Point3D(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2);
            var delta = center - camera.Position;
            double radius = Math.Sqrt(b.SizeX * b.SizeX + b.SizeY * b.SizeY + b.SizeZ * b.SizeZ) / 2;
            double depth = Vector3D.DotProduct(delta, forward);
            bool visible = (terrainVisible || !batch.IsTerrain) && depth + radius > camera.NearPlaneDistance &&
                Math.Abs(Vector3D.DotProduct(delta, right)) - horizontal * depth <= radius * Math.Sqrt(1 + horizontal * horizontal) &&
                Math.Abs(Vector3D.DotProduct(delta, up)) - vertical * depth <= radius * Math.Sqrt(1 + vertical * vertical);
            if (visible && workRadius is { } limit)
            {
                var closest = new Point3D(Math.Clamp(workCenter.X, b.X, b.X + b.SizeX), Math.Clamp(workCenter.Y, b.Y, b.Y + b.SizeY), Math.Clamp(workCenter.Z, b.Z, b.Z + b.SizeZ));
                visible = (closest - workCenter).LengthSquared <= limit * limit;
            }
            if (visible && visual.Content is null) visual.Content = batch.Model;
            else if (!visible && visual.Content is not null) visual.Content = null;
        }
    }
    private void Down(object sender, MouseButtonEventArgs e)
    {
        Focus(); last = e.GetPosition(this);
        if (e.ChangedButton is MouseButton.Right or MouseButton.Middle)
        {
            CancelDrag();
            orbitGesture = e.ChangedButton == MouseButton.Right && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
            var orbitEntity = selected ?? (visuals.Count == 1 ? entities.Values.FirstOrDefault() : null);
            if (orbitGesture && orbitEntity is not null && visuals.TryGetValue(orbitEntity.Index, out var selectedVisual))
            {
                var bounds = selectedVisual.Transform.TransformBounds(selectedVisual.Content.Bounds);
                if (!bounds.IsEmpty) SetOrbitPivot(new(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2));
            }
            CaptureMouse(); e.Handled = true; return;
        }
        if (e.ChangedButton != MouseButton.Left) return;
        var entity = Pick(e.GetPosition(viewport));
        if (entity is not null)
        {
            EntitySelected?.Invoke(entity);
            if (BeginDrag(entity, e.GetPosition(this))) CaptureMouse();
            e.Handled = true;
        }
    }
    public PresetEntity? Pick(Point point)
    {
        // WPF can miss an exact shared triangle edge at the isometric camera angle.
        // One subpixel retry keeps edge clicks selectable without broad bounds picking.
        return PickExact(point) ?? PickExact(new Point(point.X + .2, point.Y + .1));
    }
    private PresetEntity? PickExact(Point point)
    {
        PresetEntity? picked = null;
        VisualTreeHelper.HitTest(viewport, null, result =>
        {
            if (result is RayMeshGeometry3DHitTestResult batched && batched.ModelHit is GeometryModel3D model && batchHits.TryGetValue(model, out var batch))
            { picked = batch.EntityAt(batched.VertexIndex1); return HitTestResultBehavior.Stop; }
            if (result is RayMeshGeometry3DHitTestResult mesh && mesh.VisualHit is ModelVisual3D visual && entities.TryGetValue(visual, out var entity))
            { picked = entity; return HitTestResultBehavior.Stop; }
            return HitTestResultBehavior.Continue;
        }, new PointHitTestParameters(point));
        return picked;
    }
    private void Move(object sender, MouseEventArgs e)
    {
        if (!IsMouseCaptured) return;
        var point = e.GetPosition(this); var delta = point - last; last = point;
        if (dragEntity is not null) { ContinueDrag(point); e.Handled = true; return; }
        if (e.RightButton == MouseButtonState.Pressed)
            Look(delta.X, delta.Y, orbitGesture);
        if (e.MiddleButton == MouseButtonState.Pressed)
            Pan(delta.X, delta.Y, TravelMultiplier());
        e.Handled = true;
    }
    private static double TravelMultiplier() => Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 0.2 :
        Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 4 : 1;

    internal void Look(double dx, double dy, bool orbit = false)
    {
        var position = camera.Position;
        yaw = Math.IEEERemainder(yaw - dx * 0.002, Math.PI * 2);
        pitch = Math.Clamp(pitch + dy * 0.002, -1.50, 1.50);
        if (!orbit)
        {
            // Re-anchor the look target so rotating never translates the camera.
            var offset = new Vector3D(Math.Cos(pitch) * Math.Sin(yaw), Math.Sin(pitch), Math.Cos(pitch) * Math.Cos(yaw)) * distance;
            target = position - offset;
        }
        UpdateCamera();
    }
    internal void Dolly(double notches, double multiplier = 1)
    {
        var forward = camera.LookDirection; forward.Normalize();
        // Translate the entire camera rig, not its distance from an invisible pivot.
        // Equal wheel input produces equal travel and can cross the old focus point.
        target += forward * (notches * travelStep * multiplier * 2);
        UpdateCamera();
    }
    internal void Pan(double dx, double dy, double multiplier = 1)
    {
        var right = Vector3D.CrossProduct(camera.LookDirection, new(0, 1, 0)); right.Normalize();
        var up = Vector3D.CrossProduct(right, camera.LookDirection); up.Normalize();
        target += (-right * dx + up * dy) * distance / Math.Max(300, ActualHeight) * multiplier;
        UpdateCamera();
    }
    internal void SetOrbitPivot(Point3D pivot)
    {
        var offset = camera.Position - pivot;
        if (offset.Length < 0.001) return;
        target = pivot; distance = offset.Length;
        yaw = Math.Atan2(offset.X, offset.Z);
        pitch = Math.Asin(Math.Clamp(offset.Y / distance, -1, 1));
        UpdateCamera();
    }

    public static Model3DGroup Placeholder()
    {
        var group = WireBox(new Rect3D(-1, 0, -1, 2, 3, 2), Color.FromRgb(220, 153, 79));
        group.Children.Add(Box(new Rect3D(-0.35, 0, -0.35, 0.7, 3, 0.7), new DiffuseMaterial(Brushes.DarkGoldenrod)));
        group.Freeze(); return group;
    }
    private static Model3DGroup WireBox(Rect3D b, Color color)
    {
        var group = new Model3DGroup();
        if (b.IsEmpty) return group;
        var material = new EmissiveMaterial(new SolidColorBrush(color));
        double x = b.X, y = b.Y, z = b.Z, sx = Math.Max(b.SizeX, 0.1), sy = Math.Max(b.SizeY, 0.1), sz = Math.Max(b.SizeZ, 0.1);
        double t = Math.Max(0.0001, Math.Max(sx, Math.Max(sy, sz)) * 0.002);
        foreach (var yy in new[] { y, y + sy }) foreach (var zz in new[] { z, z + sz }) group.Children.Add(Box(new(x, yy, zz, sx, t, t), material));
        foreach (var xx in new[] { x, x + sx }) foreach (var zz in new[] { z, z + sz }) group.Children.Add(Box(new(xx, y, zz, t, sy, t), material));
        foreach (var xx in new[] { x, x + sx }) foreach (var yy in new[] { y, y + sy }) group.Children.Add(Box(new(xx, yy, z, t, t, sz), material));
        return group;
    }
    private static GeometryModel3D Box(Rect3D b, Material material)
    {
        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection { new(b.X,b.Y,b.Z), new(b.X+b.SizeX,b.Y,b.Z), new(b.X+b.SizeX,b.Y+b.SizeY,b.Z), new(b.X,b.Y+b.SizeY,b.Z),
                new(b.X,b.Y,b.Z+b.SizeZ), new(b.X+b.SizeX,b.Y,b.Z+b.SizeZ), new(b.X+b.SizeX,b.Y+b.SizeY,b.Z+b.SizeZ), new(b.X,b.Y+b.SizeY,b.Z+b.SizeZ) },
            TriangleIndices = new Int32Collection { 0,2,1,0,3,2,4,5,6,4,6,7,0,1,5,0,5,4,3,7,6,3,6,2,0,4,7,0,7,3,1,2,6,1,6,5 }
        };
        return new(mesh, material) { BackMaterial = material };
    }
}
