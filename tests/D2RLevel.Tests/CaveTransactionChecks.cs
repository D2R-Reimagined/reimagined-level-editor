using System.Text.Json;
using D2RLevel.Assets;
using D2RLevel.Core;

internal static class CaveTransactionChecks
{
    public static void Run(string source,string assets,string decoder,string name,GroundExtensionRequest request,string output)
    {
        output=Path.GetFullPath(output);
        if(Directory.Exists(output))throw new IOException("Use a fresh transaction-check directory.");
        Directory.CreateDirectory(output);
        string fixture=Path.Combine(output,"input");
        foreach(string path in Directory.EnumerateFiles(source,"*",SearchOption.AllDirectories))
        {
            string target=Path.Combine(fixture,Path.GetRelativePath(source,path));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(path,target);
        }
        string preset=Path.Combine(fixture,$"hd/env/preset/act1/caves/{name}.json"),map=Path.Combine(fixture,$"global/tiles/act1/caves/{name}.ds1");
        var original=File.ReadAllBytes(preset);
        var cases=new List<object>();
        foreach(bool drift in new[]{false,true})
        {
            string parent=Path.Combine(output,drift?"drift":"cancel");Directory.CreateDirectory(parent);
            string candidate=Path.Combine(parent,"candidate");
            using var cts=new CancellationTokenSource();
            using var triggered=new ManualResetEventSlim();
            using var watcher=new FileSystemWatcher(parent){NotifyFilter=NotifyFilters.DirectoryName};
            watcher.Created+=(_,e)=>
            {
                if(!Path.GetFileName(e.FullPath).StartsWith(".ground-extension-",StringComparison.Ordinal))return;
                if(drift)File.WriteAllBytes(preset,original.Concat(new byte[]{32}).ToArray());else cts.Cancel();
                triggered.Set();
            };
            watcher.EnableRaisingEvents=true;
            Exception? failure=null;
            try
            {
                GroundExtensionExporter.Export(preset,map,new AssetResolver(assets),new LevelTileset(1,["data/global/tiles/act1/caves/cave.dt1"]),request,candidate,cts.Token);
            }
            catch(Exception ex){failure=ex;}
            finally{watcher.EnableRaisingEvents=false;File.WriteAllBytes(preset,original);}
            if(!triggered.IsSet)throw new InvalidDataException("Transaction observer did not reach active staging.");
            bool expected=drift?failure is IOException&&failure.Message.Contains("input changed"):failure is OperationCanceledException;
            if(!expected||Directory.Exists(candidate)||Directory.EnumerateDirectories(parent).Any())
                throw new InvalidDataException($"Transaction failed its atomic-output check: {failure}");
            cases.Add(new{test=drift?"source change during staging":"active staging cancellation",passed=true,published=false,stageRemoved=true});
        }
        File.WriteAllText(Path.Combine(output,"validation.json"),JsonSerializer.Serialize(new{cases,gameplay="not run"},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("PASS active staging cancellation and source drift reject publication and remove temporary stages.");
    }
}
