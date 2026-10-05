namespace D2RLevel.Core;

public sealed partial class Ds1CollisionDocument
{
    /// <summary>Replace a bounded floor region and clear its walls in one undoable operation.</summary>
    public int ExtendGround(int minX, int minY, int maxX, int maxY, int donorX, int donorY)
    {
        if (History.Shared) throw new InvalidOperationException("Export a separate terrain candidate before changing a linked map.");
        if (minX >= maxX || minY >= maxY) throw new ArgumentException("Choose a nonempty tile rectangle.");
        Index(minX,minY); Index(maxX-1,maxY-1); Index(donorX,donorY);
        if (maxX >= Width || maxY >= Height)
            throw new ArgumentException("Keep the final border row and column unchanged.");
        if (ExitTiles().Any(t => t.X >= minX && t.X < maxX && t.Y >= minY && t.Y < maxY))
            throw new InvalidOperationException("The extension overlaps an entrance, exit or special marker.");
        var donor = Floors.Select(l=>Cell(l,donorX,donorY)&~Unwalkable).ToArray();
        if (donor.All(c=>(c&255)==0)) throw new ArgumentException("The source tile has no ground.");
        var changes = new List<Change>();
        for(int y=minY;y<maxY;y++)for(int x=minX;x<maxX;x++)
        {
            if(ProtectedTile?.Invoke(x,y)==true)throw new InvalidOperationException("The extension overlaps a linked footprint.");
            int index=Index(x,y);
            for(int i=0;i<Floors.Count;i++) {
                int offset=Floors[i].Offset+index*4;uint before=Read(offset);
                if(before!=donor[i])changes.Add(new(offset,before,donor[i]));
            }
            foreach(var wall in Walls) {
                int offset=wall.Offset+index*4;uint before=Read(offset);
                if(before!=0)changes.Add(new(offset,before,0));
            }
        }
        foreach(var c in changes)Write(c.Offset,c.After);
        if(changes.Count>0)RecordChanges(changes.ToArray());
        return changes.Count;
    }
}
