using System.Text.Json;
using D2RLevel.Assets;
using LSLib.Granny.GR2;

/// <summary>Research only: the reader DTO is deliberately partial, so the result is never a game export.</summary>
internal static class TerrainExportProbe
{
    public static void Run(string modelPath, string granny, string output)
    {
        Directory.CreateDirectory(output);
        string candidate = Path.Combine(output, "partial-terrain-roundtrip.gr2");
        if (File.Exists(candidate)) throw new IOException("Choose a fresh probe output directory.");
        ModelReader.ConfigureDecoder(granny);
        var model = ModelReader.Load(modelPath);
        string result;
        try
        {
            using var stream = new MemoryStream(File.ReadAllBytes(modelPath), false);
            using var reader = new GR2Reader(stream);
            var root = new D2rModelRoot(); reader.Read(root);
            foreach (var mesh in root.Meshes ?? [])
                foreach (var vertex in mesh.PrimaryVertexData?.Vertices ?? [])
                {
                    var position = vertex.Position; position.Y += 1 / (mesh.ExtendedData?.VertexScale ?? 1); vertex.Position = position;
                }
            var writer = new GR2Writer { Format = Magic.Format.LittleEndian64 };
            File.WriteAllBytes(candidate, writer.Write(root));
            var reopened = ModelReader.Load(candidate);
            bool retained = reopened.Parts.Count == model.Parts.Count && reopened.Parts.Zip(model.Parts).All(p => p.First.Positions.Length == p.Second.Positions.Length && p.First.Indices.SequenceEqual(p.Second.Indices));
            result = retained ? "Partial DTO mesh rewrite can be decoded locally. Unknown root/material/physics data is not preserved; this is NOT a game-compatible exporter." : "Partial DTO rewrite changed topology; a compatible writer is not established.";
        }
        catch (Exception ex) { result = "Mesh rewrite blocked: " + ex.GetType().Name + ": " + ex.Message; }
        File.WriteAllText(Path.Combine(output, "terrain-probe.json"), JsonSerializer.Serialize(new
        {
            Source = modelPath, SourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(modelPath))),
            Meshes = model.Parts.Count, Vertices = model.Parts.Sum(p => p.Positions.Length / 3),
            Triangles = model.Parts.Sum(p => p.Indices.Length / 3), Result = result,
            Limitations = new[] { "D2rModelRoot retains meshes only", "D2rMeshData retains VertexScale only", "No physics writer", "No biome/tilemask writer", "No in-game validation" }
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(result);
    }
}
