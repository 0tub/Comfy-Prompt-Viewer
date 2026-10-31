using System;

namespace ComfyPromptViewer;

// Hands out prewarm candidates nearest the viewport first, so the images the user is about to scroll to get
// cached before the far end of the folder. The focus can move between calls; each call re-centers on it.
//
// Positions are catalog indexes captured when the pass started. The catalog can shift a little under a
// running pass (watcher inserts), which only makes the ordering approximate: every candidate is still
// handed out exactly once.
internal sealed class PrewarmOrder
{
    // Rows behind the direction of travel count this much farther away, so lookahead wins most ties.
    private const int BehindDistanceWeight = 2;

    private readonly int[] _positions;
    private readonly bool[] _taken;
    private int _remaining;
    private int _focus = int.MinValue;
    private int _ahead;
    private int _behind;

    // positions must be ascending.
    public PrewarmOrder(int[] positions)
    {
        _positions = positions;
        _taken = new bool[positions.Length];
        _remaining = positions.Length;
    }

    public int Remaining => _remaining;

    // Returns the index into positions of the next candidate, or -1 when all are taken. direction is the
    // scroll direction: positive for down (toward higher positions), negative for up.
    public int Take(int focus, int direction)
    {
        if (_remaining == 0)
        {
            return -1;
        }

        if (focus != _focus)
        {
            _focus = focus;
            _ahead = LowerBound(focus);
            _behind = _ahead - 1;
        }

        while (_ahead < _positions.Length && _taken[_ahead])
        {
            _ahead++;
        }

        while (_behind >= 0 && _taken[_behind])
        {
            _behind--;
        }

        var downDistance = _ahead < _positions.Length ? (long)_positions[_ahead] - focus : long.MaxValue;
        var upDistance = _behind >= 0 ? (long)focus - _positions[_behind] : long.MaxValue;
        if (direction >= 0)
        {
            upDistance = Weight(upDistance);
        }
        else
        {
            downDistance = Weight(downDistance);
        }

        var index = downDistance <= upDistance ? _ahead : _behind;
        _taken[index] = true;
        _remaining--;
        return index;
    }

    private static long Weight(long distance)
    {
        return distance == long.MaxValue ? distance : distance * BehindDistanceWeight;
    }

    private int LowerBound(int value)
    {
        var low = 0;
        var high = _positions.Length;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (_positions[middle] < value)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }
}
