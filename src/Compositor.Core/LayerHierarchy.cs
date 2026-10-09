namespace Compositor.Core;

/// <summary>
/// Flat traversal and validation for the layer tree, mirroring upstream
/// LayerHierarchy: children live after their parent in the list (bottom-to-top
/// order), groups carry visibility down to their subtree, depth is capped at 64,
/// parents must be groups, and cycles are rejected.
/// </summary>
public static class LayerHierarchy
{
    public const int MaxDepth = 64;

    public sealed record Entry(Layer Layer, int Depth, bool Visible);

    /// <summary>
    /// Depth-first, parent-before-children traversal in list order.
    /// A layer is visible only when every ancestor group is visible too.
    /// </summary>
    public static List<Entry> Entries(IReadOnlyList<Layer> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        var children = GroupByParent(layers);
        var result = new List<Entry>(layers.Count);
        Visit(children, string.Empty, depth: 0, visible: true, result);
        return result;
    }

    private static Dictionary<string, List<Layer>> GroupByParent(IReadOnlyList<Layer> layers)
    {
        // String keys: nullable Guid is not a valid Dictionary TKey (notnull constraint).
        var children = new Dictionary<string, List<Layer>>(StringComparer.Ordinal);
        foreach (var layer in layers)
        {
            var key = layer.ParentId?.ToString("D") ?? string.Empty;
            if (!children.TryGetValue(key, out var list))
            {
                list = new List<Layer>();
                children[key] = list;
            }
            list.Add(layer);
        }
        return children;
    }

    private static void Visit(
        Dictionary<string, List<Layer>> children, string parent, int depth, bool visible, List<Entry> result)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        foreach (var layer in children.GetValueOrDefault(parent) ?? new List<Layer>())
        {
            var effective = visible && layer.IsVisible;
            result.Add(new Entry(layer, depth, effective));
            if (layer.IsGroup)
            {
                Visit(children, layer.Id.ToString("D"), depth + 1, effective, result);
            }
        }
    }

    /// <summary>Visible, non-group layers in compositing order (bottom to top).</summary>
    public static List<Layer> VisibleLayers(IReadOnlyList<Layer> layers) =>
        Entries(layers).Where(e => e.Visible && !e.Layer.IsGroup).Select(e => e.Layer).ToList();

    /// <summary>Descendants of a layer at any depth (excluding itself).</summary>
    public static HashSet<Guid> DescendantIds(IReadOnlyList<Layer> layers, Guid id)
    {
        ArgumentNullException.ThrowIfNull(layers);
        var children = GroupByParent(layers);
        var result = new HashSet<Guid>();
        var pending = new Stack<Guid>();
        pending.Push(id);
        while (pending.Count > 0)
        {
            foreach (var child in children.GetValueOrDefault(pending.Pop().ToString("D")) ?? new List<Layer>())
            {
                if (result.Add(child.Id))
                {
                    pending.Push(child.Id);
                }
            }
        }
        return result;
    }

    /// <summary>
    /// The same layers reordered so every parent precedes its children, which is the order the
    /// format documents and the one the load path relies on. Sibling order is preserved and the
    /// traversal is stable, so a list already in canonical order comes back unchanged.
    ///
    /// Layers whose parent is absent from the list are kept, in their original relative order,
    /// after the reachable ones. Validate rejects them, and dropping them here would turn a
    /// diagnosable file into a silently lossy one.
    /// </summary>
    public static List<Layer> Normalize(IReadOnlyList<Layer> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        var children = GroupByParent(layers);
        var emitted = new HashSet<Guid>();
        var result = new List<Layer>(layers.Count);
        Emit(children, string.Empty, result, emitted);
        foreach (var layer in layers)
        {
            if (emitted.Add(layer.Id))
            {
                result.Add(layer);
            }
        }

        return result;
    }

    /// <summary>
    /// Appends <paramref name="parent"/>'s children depth-first. <paramref name="emitted"/> is
    /// what makes this terminate on a cyclic graph: a layer can only be added once, so a parent
    /// that reaches itself stops instead of recursing forever.
    /// </summary>
    private static void Emit(
        Dictionary<string, List<Layer>> children,
        string parent,
        List<Layer> result,
        HashSet<Guid> emitted)
    {
        foreach (var layer in children.GetValueOrDefault(parent) ?? new List<Layer>())
        {
            if (!emitted.Add(layer.Id))
            {
                continue;
            }

            result.Add(layer);
            if (layer.IsGroup)
            {
                Emit(children, layer.Id.ToString("D"), result, emitted);
            }
        }
    }

    /// <summary>
    /// Structural validation: unique ids, groups carry no pixels, a parent id
    /// references an existing group, no cycles, depth within the cap, and every
    /// parent preceding its children in list order. Throws
    /// InvalidOperationException with the reason on the first violation.
    /// </summary>
    public static void Validate(IReadOnlyList<Layer> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        var byId = new Dictionary<Guid, Layer>(layers.Count);
        var indexOf = new Dictionary<Guid, int>(layers.Count);
        for (var i = 0; i < layers.Count; i++)
        {
            var layer = layers[i];
            if (!byId.TryAdd(layer.Id, layer))
            {
                throw new InvalidOperationException($"Duplicate layer id {layer.Id}.");
            }
            indexOf[layer.Id] = i;
            if (layer.IsGroup && layer.Pixels is not null)
            {
                throw new InvalidOperationException($"Group layer '{layer.Name}' must not carry pixels.");
            }
        }

        foreach (var layer in layers)
        {
            var seen = new HashSet<Guid> { layer.Id };
            var parent = layer.ParentId;
            var depth = 0;
            while (parent is { } id)
            {
                if (++depth > MaxDepth)
                {
                    throw new InvalidOperationException($"Hierarchy deeper than {MaxDepth} at '{layer.Name}'.");
                }
                if (!seen.Add(id))
                {
                    throw new InvalidOperationException($"Cycle in layer hierarchy at '{layer.Name}'.");
                }
                if (!byId.TryGetValue(id, out var node))
                {
                    throw new InvalidOperationException($"Layer '{layer.Name}' references missing parent {id}.");
                }
                if (!node.IsGroup)
                {
                    throw new InvalidOperationException($"Layer '{layer.Name}' has non-group parent '{node.Name}'.");
                }
                parent = node.ParentId;
            }

            // Order is part of the invariant, not just structure. The format documents it and the
            // load path reads layers in order, so a list that puts a child before its parent is a
            // project that saves and then cannot be reopened. Checked here so the write path
            // refuses it instead of writing a file that only fails later.
            if (layer.ParentId is { } direct && indexOf.TryGetValue(direct, out var parentIndex)
                && parentIndex > indexOf[layer.Id])
            {
                throw new InvalidOperationException(
                    $"Layer '{layer.Name}' appears before its parent '{byId[direct].Name}' in the stack.");
            }
        }
    }
}
