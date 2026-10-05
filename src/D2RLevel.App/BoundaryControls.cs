using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using D2RLevel.Core;
using D2RLevel.Assets;
using Microsoft.Win32;

namespace D2RLevel.App;

public partial class MainWindow
{
    private async void BuildBoundary_Click(object sender,RoutedEventArgs e)
    {
        if(loading is not null)return;
        if(document is null || pairedScene?.Collision is null || resolver is null || placementLinks is null)
        {Status.Text=L.T("Open a paired candidate and calibrate its grid before building a boundary.");return;}
        if(document.IsDirty || pairedScene.Collision.Document.IsDirty || placementLinks.HasMetadataChanges || connectionEdits?.IsDirty==true)
        {Status.Text=L.T("Save your scene, links and entrance edits before exporting a boundary.");return;}
        BoundaryRevision? revision;
        var mapDocument=pairedScene.Collision.Document;
        var previewCollision=pairedScene.Collision;
        try {
            revision=BoundaryRevision.Load(document.SourcePath);
            if(revision is not null){
                var previewScene=PresetDocument.Load(document.SourcePath);
                mapDocument=Ds1CollisionDocument.Load(pairedScene.Ds1Path);
                var previewLinks=new PlacementLinks(previewScene,mapDocument);
                revision.Restore(previewScene,mapDocument,previewLinks);
                previewCollision=new LegacyCollision(mapDocument,pairedScene.Dt1Paths.SelectMany(LegacyCollision.ReadTiles));
            }
        }catch(Exception ex){Error(ex);return;}
        var panel=new StackPanel{Margin=new Thickness(18)};
        panel.Children.Add(new TextBlock{Text=L.T("Build boundary (experimental)"),FontSize=20});
        panel.Children.Add(new TextBlock{Text=L.T("Creates wall pieces, blocking and outline tiles from this map's floor types. Leave a gate at least three tiles wide. The exported editor project includes its tileset; game registration is still required."),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,8)});
        var suggested=revision?.Request ?? BoundaryAuthoring.Suggest(previewCollision);
        suggested??=new BoundaryRequest(1,1,Math.Min(11,mapDocument.Width-1),Math.Min(10,mapDocument.Height-1),BoundarySide.North,3,3);
        panel.Children.Add(new TextBlock{Text=$"{mapDocument.Width-1} × {mapDocument.Height-1} tiles · Act {mapDocument.Act}"});
        var fields=new List<TextBox>();
        foreach(var (label,value) in new[]{(L.T("Minimum X"),suggested.MinX.ToString()),(L.T("Minimum Y"),suggested.MinY.ToString()),(L.T("Maximum X (exclusive)"),suggested.MaxX.ToString()),(L.T("Maximum Y (exclusive)"),suggested.MaxY.ToString()),(L.T("Gate start tile"),suggested.GateStart.ToString()),(L.T("Gate width"),suggested.GateWidth.ToString()),(L.T("Wall height"),suggested.Height.ToString(CultureInfo.InvariantCulture))})
        {panel.Children.Add(new TextBlock{Text=label});var field=new TextBox{Text=value};fields.Add(field);panel.Children.Add(field);}
        var side=new ComboBox{ItemsSource=Enum.GetValues<BoundarySide>(),SelectedItem=suggested.GateSide}.WithReadableItems();panel.Children.Add(side);
        panel.Children.Add(new TextBlock{Text=L.T("Wall model path")});
        var model=new TextBox{Text=revision?.WallModel ?? "data/hd/env/model/act1/caves/act1_caves_walls/R_wall01.model"};panel.Children.Add(model);
        var status=new TextBlock{TextWrapping=TextWrapping.Wrap};panel.Children.Add(status);
        var export=new Button{Content=revision is null?L.T("Export boundary"):L.T("Regenerate boundary"),Margin=new Thickness(0,12,0,0)};panel.Children.Add(export);
        var custom=new CheckBox{Content=L.T("Custom outline"),IsChecked=suggested.Corners is not null,Margin=new Thickness(0,8,0,4)};
        panel.Children.Insert(3,custom);
        var right=new StackPanel{Margin=new Thickness(12)};
        right.Children.Add(new TextBlock{Text=L.T("Outline corners: one X,Y pair per line. Click the map to append a corner. The last corner connects to the first. Use horizontal and vertical segments."),TextWrapping=TextWrapping.Wrap});
        var corners=new TextBox{AcceptsReturn=true,Height=105,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
            Text=suggested.Corners is not null?string.Join("\n",suggested.Corners.Select(c=>$"{c.X},{c.Y}")):$"{suggested.MinX},{suggested.MinY}\n{suggested.MaxX-1},{suggested.MinY}\n{suggested.MaxX-1},{suggested.MaxY-1}\n{suggested.MinX},{suggested.MaxY-1}"};right.Children.Add(corners);
        var clear=new Button{Content=L.T("Clear corners")};right.Children.Add(clear);
        right.Children.Add(new TextBlock{Text=L.T("Gate segment (numbered from 1); Gate start tile becomes an offset from that segment's first corner."),TextWrapping=TextWrapping.Wrap});
        var gateEdge=new TextBox{Text=(suggested.GateEdge+1).ToString()};right.Children.Add(gateEdge);
        var preview=new BoundaryOutlineMap(previewCollision){Height=300};right.Children.Add(preview);
        right.Children.Add(new TextBlock{Text=L.T("Gold: blocking walls. Cyan: open gate. Gray: clear floor. Dark: unavailable floor. Preview does not create HD terrain."),TextWrapping=TextWrapping.Wrap});
        var layout=new Grid();layout.ColumnDefinitions.Add(new(){Width=new GridLength(430)});layout.ColumnDefinitions.Add(new());
        layout.Children.Add(new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});Grid.SetColumn(right,1);layout.Children.Add(right);
        var dialog=new Window{Owner=this,Title=L.T("Build boundary (experimental)"),Width=1020,Height=800,MinWidth=900,MinHeight=740,
            Background=Background,Foreground=Foreground,Content=layout,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        LinkedTile[] ReadCorners()=>corners.Text.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries).Select(line=>{
            var values=line.Split(',');if(values.Length!=2)throw new FormatException("Enter one X,Y corner per line.");
            return new LinkedTile(int.Parse(values[0],CultureInfo.InvariantCulture),int.Parse(values[1],CultureInfo.InvariantCulture));}).ToArray();
        BoundaryRequest Read(){
            int N(int i)=>int.Parse(fields[i].Text,CultureInfo.InvariantCulture);
            return new(N(0),N(1),N(2),N(3),(BoundarySide)side.SelectedItem,N(4),N(5),double.Parse(fields[6].Text,CultureInfo.InvariantCulture),
                custom.IsChecked==true?ReadCorners():null,int.Parse(gateEdge.Text,CultureInfo.InvariantCulture)-1);
        }
        bool busy=false;
        void Refresh(){
            if(busy)return;
            for(int i=0;i<4;i++)fields[i].IsEnabled=custom.IsChecked!=true;
            side.IsEnabled=custom.IsChecked!=true;right.IsEnabled=custom.IsChecked==true;
            preview.Layout=null;preview.Corners=[];
            try{
                if(custom.IsChecked==true){
                    preview.Corners=ReadCorners();var r=Read();var shape=BoundaryPolygon.Layout(mapDocument,r);
                    BoundaryPolygon.ValidateFloor(previewCollision,shape);preview.Layout=shape;
                    BoundaryAuthoring.Plan(mapDocument,r,new(new(0,0,0),new(1,1,1)),placementLinks.Calibration.UnitsPerTile);
                    status.Text=$"{shape.Walls.Length} walls · {shape.Gate.Length} gate tiles";
                }else status.Text="";
                export.IsEnabled=true;
            }catch(Exception ex){status.Text=ex.Message;export.IsEnabled=false;}
            preview.InvalidateVisual();
        }
        foreach(var field in fields)field.TextChanged+=(_,_)=>Refresh();
        corners.TextChanged+=(_,_)=>Refresh();gateEdge.TextChanged+=(_,_)=>Refresh();custom.Checked+=(_,_)=>Refresh();custom.Unchecked+=(_,_)=>Refresh();
        clear.Click+=(_,_)=>corners.Text="";
        preview.Picked+=(x,y)=>{if(!busy)corners.Text+=(string.IsNullOrWhiteSpace(corners.Text)?"":"\n")+$"{x},{y}";};
        Refresh();
        string? output=null;
        export.Click+=async(_,_)=> {
            try {
                var request=Read();
                var picker=new OpenFolderDialog{Title=L.T("Choose the parent folder for a new boundary")};if(picker.ShowDialog(dialog)!=true)return;
                string destination=Path.Combine(picker.FolderName,"boundary-"+Guid.NewGuid().ToString("N")[..8]);
                string preset=document.SourcePath,map=pairedScene.Ds1Path,wall=model.Text;var assets=resolver;
                var context=new LevelTileset(pairedScene.Mask,pairedScene.Dt1Paths.Select(p=>"data/global/tiles/"+
                    (PresetPairing.Split(p,"global/tiles")?.Relative ?? throw new InvalidDataException("Tileset outside data.")).Replace('\\','/')).ToArray());
                busy=true;panel.IsEnabled=false;right.IsEnabled=false;export.IsEnabled=false;status.Text=L.T("Preparing boundary and collision…");
                output=await Task.Run(()=>BoundaryExporter.Export(preset,map,assets,request,wall,destination,tileset:context));busy=false;dialog.Close();
            }catch(Exception ex){busy=false;panel.IsEnabled=true;right.IsEnabled=custom.IsChecked==true;Refresh();status.Text=ex.Message;}
        };
        dialog.Closing+=(_,args)=>{if(busy)args.Cancel=true;};
        dialog.ShowDialog();
        if(output is null)return;
        try{
            await LoadWorkspace(PresetPairing.Split(output,"hd/env/preset")!.Value.DataRoot);
            await OpenWorkspaceScene(workspaceScenes.Single(s=>Path.GetFullPath(s.JsonPath).Equals(Path.GetFullPath(output),StringComparison.OrdinalIgnoreCase)));
            Status.Text=L.T("Boundary exported. Inspect the wall pieces, blocking and open gate before gameplay qualification.");
        }catch(Exception ex){Error(ex);}
    }
}
