using System.Text.Json;
using D2RLevel.Core;

internal static class CaveWorkflowAudit
{
    public static void Compare(string source, string candidate, string dt1, string output)
    {
        var tiles = LegacyCollision.ReadTiles(dt1);
        var a = new LegacyCollision(Ds1CollisionDocument.Load(source), tiles);
        var b = new LegacyCollision(Ds1CollisionDocument.Load(candidate), tiles);
        int Count(LegacyCollision c, Func<TileCollision, bool> predicate) =>
            Enumerable.Range(0, c.Document.Height).Sum(y => Enumerable.Range(0, c.Document.Width).Count(x => predicate(c.At(x,y))));
        var introduced = new List<int[]>();
        for (int y=0;y<a.Document.Height;y++) for(int x=0;x<a.Document.Width;x++)
            if (!a.At(x,y).Unresolved && b.At(x,y).Unresolved) introduced.Add([x,y]);
        var result = new { source, candidate, sourceUnresolved=Count(a,c=>c.Unresolved),
            candidateUnresolved=Count(b,c=>c.Unresolved), introducedUnresolved= introduced,
            sourceOpen=Count(a,c=>!c.NoFloor&&!c.Unresolved&&c.BlockedSubtiles==0),
            candidateOpen=Count(b,c=>!c.NoFloor&&!c.Unresolved&&c.BlockedSubtiles==0), gameplay="not run" };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output,JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(File.ReadAllText(output));
        if(introduced.Count>0) throw new InvalidDataException("Export introduced unresolved collision cells.");
    }
}
