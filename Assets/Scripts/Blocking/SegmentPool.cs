using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Simple pool for RibbonSegment objects.
/// Not a MonoBehaviour. Do NOT attach this to anything.
/// </summary>
public class SegmentPool
{
    private readonly Queue<RibbonSegment> _pool = new Queue<RibbonSegment>();
    private readonly RibbonSegment _prefab;
    private readonly Transform _parent;

    public SegmentPool(RibbonSegment prefab, int initialCount, Transform parent = null)
    {
        _prefab = prefab;
        _parent = parent;

        if (_prefab == null) return;

        for (int i = 0; i < Mathf.Max(0, initialCount); i++)
        {
            var seg = Object.Instantiate(_prefab, _parent);
            seg.gameObject.SetActive(false);
            _pool.Enqueue(seg);
        }
    }

    public RibbonSegment Get()
    {
        if (_prefab == null) return null;

        if (_pool.Count > 0)
            return _pool.Dequeue();

        var seg = Object.Instantiate(_prefab, _parent);
        seg.gameObject.SetActive(false);
        return seg;
    }

    public void Release(RibbonSegment seg)
    {
        if (seg == null) return;
        seg.gameObject.SetActive(false);
        _pool.Enqueue(seg);
    }

    public void Clear()
    {
        _pool.Clear();
    }
}