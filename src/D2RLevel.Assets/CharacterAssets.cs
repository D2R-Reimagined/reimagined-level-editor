using D2RLevel.Core;

namespace D2RLevel.Assets;

/// <summary>Where a character model's rig and animation set live, if it has them.</summary>
public sealed record CharacterAssetPaths(string RigPath, string AnimationsPath);

/// <summary>
/// Locates the rig and animation set beside a character mesh. D2R keeps them in sibling
/// folders of the model -- <c>skeleton/&lt;name&gt;.skeleton</c> and
/// <c>animation/combined.animations</c>. Not every character folder has both: variants
/// commonly reuse another character's rig, and those report as unanimated rather than
/// being matched to a rig they may not belong to.
/// </summary>
public static class CharacterAssets
{
    public static CharacterAssetPaths? Find(string modelPath, AssetResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        var model = resolver.Resolve(resolver.ExtractionPath(modelPath));
        var folder = Path.GetDirectoryName(model);
        if (folder is null) return null;
        var animations = Path.Combine(folder, "animation", "combined.animations");
        if (!File.Exists(animations)) return null;
        var skeletons = Path.Combine(folder, "skeleton");
        if (!Directory.Exists(skeletons)) return null;
        var rig = Directory.EnumerateFiles(skeletons, "*.skeleton").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        return rig is null ? null : new(rig, animations);
    }
}
