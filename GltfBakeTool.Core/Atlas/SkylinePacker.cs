namespace GltfBakeTool.Core.Atlas;

public readonly record struct PackRect(int X, int Y, int W, int H)
{
    public int Right => X + W;
    public int Bottom => Y + H;
}

/// <summary>Bottom-left skyline rectangle packer (no rotation).</summary>
public sealed class SkylinePacker
{
    private readonly int _width, _height;
    private readonly List<(int x, int y, int w)> _skyline = new();

    public SkylinePacker(int width, int height)
    {
        _width = width;
        _height = height;
        _skyline.Add((0, 0, width));
    }

    public bool TryInsert(int w, int h, out PackRect rect)
    {
        int bestY = int.MaxValue, bestX = int.MaxValue, bestIndex = -1;
        for (int i = 0; i < _skyline.Count; i++)
        {
            if (TryFit(i, w, h, out int y))
            {
                if (y + h < bestY || (y + h == bestY && _skyline[i].x < bestX))
                {
                    bestY = y + h;
                    bestX = _skyline[i].x;
                    bestIndex = i;
                }
            }
        }
        if (bestIndex < 0) { rect = default; return false; }

        rect = new PackRect(bestX, bestY - h, w, h);
        AddLevel(bestIndex, rect);
        return true;
    }

    private bool TryFit(int index, int w, int h, out int y)
    {
        int x = _skyline[index].x;
        y = 0;
        if (x + w > _width) return false;
        int widthLeft = w;
        int i = index;
        while (widthLeft > 0)
        {
            if (i >= _skyline.Count) return false;
            y = Math.Max(y, _skyline[i].y);
            if (y + h > _height) return false;
            widthLeft -= _skyline[i].w;
            i++;
        }
        return true;
    }

    private void AddLevel(int index, PackRect r)
    {
        _skyline.Insert(index, (r.X, r.Bottom, r.W));
        for (int i = index + 1; i < _skyline.Count; i++)
        {
            var cur = _skyline[i];
            var prev = _skyline[i - 1];
            if (cur.x < prev.x + prev.w)
            {
                int shrink = prev.x + prev.w - cur.x;
                if (cur.w <= shrink) { _skyline.RemoveAt(i); i--; }
                else { _skyline[i] = (cur.x + shrink, cur.y, cur.w - shrink); break; }
            }
            else break;
        }
        // merge equal heights
        for (int i = 0; i < _skyline.Count - 1; i++)
        {
            if (_skyline[i].y == _skyline[i + 1].y)
            {
                _skyline[i] = (_skyline[i].x, _skyline[i].y, _skyline[i].w + _skyline[i + 1].w);
                _skyline.RemoveAt(i + 1);
                i--;
            }
        }
    }

    /// <summary>Packs all sizes (sorted by height, then width) into a width×height bin. Returns null when they don't fit.</summary>
    public static PackRect[]? PackAll(IReadOnlyList<(int w, int h)> sizes, int width, int height)
    {
        var order = Enumerable.Range(0, sizes.Count)
            .OrderByDescending(i => sizes[i].h)
            .ThenByDescending(i => sizes[i].w)
            .ToArray();
        var packer = new SkylinePacker(width, height);
        var result = new PackRect[sizes.Count];
        foreach (int i in order)
        {
            if (!packer.TryInsert(sizes[i].w, sizes[i].h, out var r)) return null;
            result[i] = r;
        }
        return result;
    }
}
