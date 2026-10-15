using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using D2RLevel.Core;

namespace D2RLevel.App;

/// <summary>Tile-centre outline authoring preview; never mutates the scene.</summary>
internal sealed class BoundaryOutlineMap(LegacyCollision collision) : FrameworkElement
{
    public LinkedTile[] Corners {get;set;}=[];
    public BoundaryPolygonLayout? Layout {get;set;}
    public event Action<int,int>? Picked;
    private double CellSize=>Math.Max(.1,Math.Min((ActualWidth-30)/collision.Document.Width,(ActualHeight-30)/collision.Document.Height));
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(19,27,36)),null,new(0,0,ActualWidth,ActualHeight));
        double s=CellSize;
        var walls=Layout?.Walls.ToHashSet()??[];var gates=Layout?.Gate.ToHashSet()??[];
        for(int y=0;y<collision.Document.Height-1;y++)for(int x=0;x<collision.Document.Width-1;x++){
            var c=collision.At(x,y);
            Brush brush=c is {NoFloor:false,Unresolved:false,VariantDependent:false,BlockedSubtiles:0}?Brushes.SlateGray:Brushes.DarkSlateGray;
            if(walls.Contains(new(x,y)))brush=Brushes.Goldenrod;if(gates.Contains(new(x,y)))brush=Brushes.Cyan;
            dc.DrawRectangle(brush,new Pen(Brushes.Black,.3),new(15+x*s,15+y*s,s,s));
        }
        Point Centre(LinkedTile p)=>new(15+(p.X+.5)*s,15+(p.Y+.5)*s);
        if(Corners.Length>1)for(int i=0;i<Corners.Length;i++){
            var a=Centre(Corners[i]);var b=Centre(Corners[(i+1)%Corners.Length]);
            dc.DrawLine(new Pen(Brushes.White,1),a,b);
            var label=new FormattedText((i+1).ToString(CultureInfo.InvariantCulture),CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,new Typeface("Segoe UI"),12,Brushes.White,VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(label,new((a.X+b.X)/2+3,(a.Y+b.Y)/2+3));
        }
        foreach(var p in Corners)dc.DrawEllipse(Brushes.White,null,Centre(p),3,3);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var p=e.GetPosition(this);int x=(int)Math.Floor((p.X-15)/CellSize),y=(int)Math.Floor((p.Y-15)/CellSize);
        if(x>=0&&y>=0&&x<collision.Document.Width-1&&y<collision.Document.Height-1)Picked?.Invoke(x,y);
        e.Handled=true;
    }
}
