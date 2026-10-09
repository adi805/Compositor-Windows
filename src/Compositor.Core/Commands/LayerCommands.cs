using Compositor.Core;
using Compositor.Core.Imaging;
using Compositor.Core.Selection;

namespace Compositor.Core.Commands;

/// <summary>Adds a layer to a document, removable via undo.</summary>
public sealed class AddLayerCommand : IUndoCommand
{
    private readonly Document _doc;
    private readonly Layer _layer;

    public AddLayerCommand(Document doc, Layer layer)
    {
        _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        _layer = layer ?? throw new ArgumentNullException(nameof(layer));
    }

    public void Redo() => _doc.AddLayer(_layer);
    public void Undo() => _doc.RemoveLayer(_layer);
}

/// <summary>Removes a layer from a document, restorable via undo.</summary>
public sealed class RemoveLayerCommand : IUndoCommand
{
    private readonly Document _doc;
    private readonly Layer _layer;
    private readonly int _index;

    public RemoveLayerCommand(Document doc, Layer layer)
    {
        _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        _layer = layer ?? throw new ArgumentNullException(nameof(layer));
        _index = doc.Layers.IndexOf(layer);
        if (_index < 0) throw new ArgumentException("Layer is not in this document", nameof(layer));
    }

    public void Undo()
    {
        _doc.Layers.Insert(Math.Min(_index, _doc.Layers.Count), _layer);
    }

    public void Redo() => _doc.RemoveLayer(_layer);
}

/// <summary>
/// Wraps the given layers into a new group inserted at the stack position of
/// the topmost one (upstream groupSelectedLayers): children keep their list
/// order, land after the group (parent-before-children), and their old parents
/// are restored on undo. Also used by "New Folder" with a single empty selection.
/// </summary>
public sealed class GroupLayersCommand : IUndoCommand
{
    private readonly Document _doc;
    private readonly IReadOnlyList<Layer> _members;
    private readonly int[] _memberIndices;
    private readonly Guid?[] _oldParents;
    private readonly Layer _group;
    private readonly int _insertIndex;

    public GroupLayersCommand(Document doc, IReadOnlyList<Layer> members, string? groupName = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(members);
        if (members.Count == 0)
        {
            throw new ArgumentException("Group needs at least one layer.", nameof(members));
        }
        _doc = doc;
        _members = members;
        _memberIndices = members.Select(m => doc.Layers.IndexOf(m)).ToArray();
        if (_memberIndices.Any(i => i < 0))
        {
            throw new ArgumentException("All members must belong to the document.", nameof(members));
        }
        _oldParents = members.Select(m => m.ParentId).ToArray();

        // "Folder N" naming, first free number (upstream parity).
        var baseName = string.IsNullOrWhiteSpace(groupName) ? null : groupName.Trim();
        if (baseName is null)
        {
            var taken = doc.Layers.Select(l => l.Name).ToHashSet(StringComparer.Ordinal);
            var number = 1;
            while (taken.Contains($"Folder {number}"))
            {
                number++;
            }
            baseName = $"Folder {number}";
        }
        _group = Layer.Group(baseName);
        // Common parent: the members' shared parent, when they all agree.
        var first = members[0].ParentId;
        _group.ParentId = members.All(m => m.ParentId == first) ? first : null;
        _insertIndex = _memberIndices.Max();
    }

    public void Redo()
    {
        foreach (var layer in _members)
        {
            _doc.Layers.Remove(layer);
            layer.ParentId = _group.Id;
        }
        var before = _memberIndices.Count(i => i < _insertIndex);
        var at = Math.Clamp(_insertIndex - before, 0, _doc.Layers.Count);
        _doc.Layers.Insert(at, _group);
        // Children follow their parent in list order (parent-before-children).
        for (var i = 0; i < _members.Count; i++)
        {
            _doc.Layers.Insert(Math.Min(at + 1 + i, _doc.Layers.Count), _members[i]);
        }
    }

    public void Undo()
    {
        _doc.Layers.Remove(_group);
        for (var i = 0; i < _members.Count; i++)
        {
            _doc.Layers.Remove(_members[i]);
            _members[i].ParentId = _oldParents[i];
        }
        for (var i = 0; i < _members.Count; i++)
        {
            _doc.Layers.Insert(Math.Min(_memberIndices[i], _doc.Layers.Count), _members[i]);
        }
    }
}

/// <summary>
/// Moves a layer within the stacking order (list index), restorable on undo.
///
/// A group moves together with its descendants. Moving the group alone left its children behind,
/// and when the group landed past one of them the list held a child before its parent: the project
/// saved without complaint and then refused to reopen, so an ordinary drag could overwrite a
/// usable file with an unreadable one. The block keeps its internal order and the layer's final
/// index is the index of the block, clamped so the block stays inside the list.
/// </summary>
public sealed class ReorderLayerCommand : IUndoCommand
{
    private readonly Document _doc;
    private readonly Layer _layer;
    private readonly int _to;
    private readonly List<Layer> _block;
    private readonly int[] _blockIndices;
    private readonly int _othersBefore;

    public ReorderLayerCommand(Document doc, Layer layer, int newIndex)
    {
        _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        _layer = layer ?? throw new ArgumentNullException(nameof(layer));
        if (doc.Layers.IndexOf(layer) < 0)
        {
            throw new ArgumentException("Layer is not in this document.", nameof(layer));
        }

        _block = Block(doc, layer);
        _blockIndices = _block.Select(l => doc.Layers.IndexOf(l)).ToArray();
        _to = Math.Clamp(newIndex, 0, doc.Layers.Count - 1);

        // How many of the block's other members already sit before the target index. They leave the
        // list with the block, so the insertion point shifts back by that much: without this a
        // three-layer group could not be moved to the bottom, because the target would be past the
        // end of the list once its own members were removed.
        var others = new HashSet<Layer>(_block);
        others.Remove(_layer);
        var count = 0;
        for (var i = 0; i < _to && i < doc.Layers.Count; i++)
        {
            if (others.Contains(doc.Layers[i]))
            {
                count++;
            }
        }

        _othersBefore = count;
    }

    public void Redo()
    {
        foreach (var member in _block)
        {
            _doc.Layers.Remove(member);
        }

        var at = Math.Clamp(_to - _othersBefore, 0, _doc.Layers.Count);
        for (var i = 0; i < _block.Count; i++)
        {
            _doc.Layers.Insert(at + i, _block[i]);
        }
    }

    public void Undo()
    {
        foreach (var member in _block)
        {
            _doc.Layers.Remove(member);
        }

        for (var i = 0; i < _block.Count; i++)
        {
            _doc.Layers.Insert(Math.Min(_blockIndices[i], _doc.Layers.Count), _block[i]);
        }
    }

    /// <summary>
    /// The layer plus every descendant, in list order, so the group and its children travel as one
    /// block. A layer with no descendants yields a one-element block, which is the plain
    /// single-layer move this command has always performed.
    /// </summary>
    private static List<Layer> Block(Document doc, Layer layer)
    {
        var descendants = LayerHierarchy.DescendantIds(doc.Layers, layer.Id);
        return doc.Layers.Where(l => l == layer || descendants.Contains(l.Id)).ToList();
    }
}

/// <summary>Sets one layer's blend mode and/or opacity, restoring both on undo.</summary>
public sealed class SetLayerAppearanceCommand : IUndoCommand
{
    private readonly Layer _layer;
    private readonly BlendMode _oldBlend;
    private readonly double _oldOpacity;
    private readonly BlendMode _newBlend;
    private readonly double _newOpacity;

    public SetLayerAppearanceCommand(Layer layer, BlendMode? blend, double? opacity)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if (opacity is { } o && (!double.IsFinite(o) || o < 0.0 || o > 1.0))
        {
            throw new ArgumentOutOfRangeException(nameof(opacity), "Opacity must be within [0, 1].");
        }
        _layer = layer;
        _oldBlend = layer.Blend;
        _oldOpacity = layer.Opacity;
        _newBlend = blend ?? layer.Blend;
        _newOpacity = opacity ?? layer.Opacity;
    }

    public void Redo()
    {
        _layer.Blend = _newBlend;
        _layer.Opacity = _newOpacity;
    }

    public void Undo()
    {
        _layer.Blend = _oldBlend;
        _layer.Opacity = _oldOpacity;
    }
}

/// <summary>
/// Composites the given layers (bottom to top, in their stacking order) with
/// their blend modes and opacity baked in, trims the result to its content
/// bounds, replaces the sources with that one pixel layer at the anchor's
/// stack position (upstream mergeLayers / ⌘E). One undo step restores all.
/// </summary>
public sealed class MergeLayersCommand : IUndoCommand
{
    private readonly Document _doc;
    private readonly IReadOnlyList<Layer> _sources;
    private readonly int[] _sourceIndices;
    private readonly Layer _merged;
    private readonly int _anchorIndex;

    public MergeLayersCommand(Document doc, IReadOnlyList<Layer> sources, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count < 2)
        {
            throw new ArgumentException("Merge needs at least two layers.", nameof(sources));
        }
        _doc = doc;
        _sources = sources;
        _sourceIndices = sources.Select(s => doc.Layers.IndexOf(s)).ToArray();
        if (_sourceIndices.Any(i => i < 0))
        {
            throw new ArgumentException("All sources must belong to the document.", nameof(sources));
        }
        if (sources.Any(s => s.Pixels is null))
        {
            throw new ArgumentException("Merge sources must have pixels (blank layers cannot merge).", nameof(sources));
        }

        _anchorIndex = _sourceIndices.Max(); // topmost source's slot
        var baked = SurfaceOps.CompositeStack(sources, doc.Width, doc.Height);
        var bounds = SurfaceOps.ContentBounds(baked.Pixels, doc.Width, doc.Height);
        if (bounds is not { } box)
        {
            throw new InvalidOperationException("Merge produced no pixels (fully transparent sources).");
        }
        var cropped = SurfaceOps.Crop(baked, box.X, box.Y, box.Width, box.Height);
        var bottom = sources[0];
        _merged = new Layer(string.IsNullOrWhiteSpace(name) ? bottom.Name : name.Trim())
        {
            Pixels = cropped,
            ParentId = bottom.ParentId,
            Transform = new LayerTransform(box.X, box.Y, box.Width, box.Height, 0, false, false),
        };
    }

    public void Redo()
    {
        foreach (var layer in _sources)
        {
            _doc.Layers.Remove(layer);
        }
        var before = _sourceIndices.Count(i => i < _anchorIndex);
        _doc.Layers.Insert(Math.Min(_anchorIndex - before, _doc.Layers.Count), _merged);
    }

    public void Undo()
    {
        _doc.Layers.Remove(_merged);
        for (var i = 0; i < _sources.Count; i++)
        {
            _doc.Layers.Insert(Math.Min(_sourceIndices[i], _doc.Layers.Count), _sources[i]);
        }
    }
}

/// <summary>Inserts a new group node at a stack position, removable via undo (upstream addGroup).</summary>
public sealed class AddGroupCommand : IUndoCommand
{
    private readonly Document _doc;
    private readonly Layer _group;
    private readonly int _index;

    public AddGroupCommand(Document doc, Layer group, int index)
    {
        _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        _group = group ?? throw new ArgumentNullException(nameof(group));
        if (!group.IsGroup)
        {
            throw new ArgumentException("Only group layers can be inserted by this command.", nameof(group));
        }
        _index = Math.Clamp(index, 0, doc.Layers.Count);
    }

    public void Redo() => _doc.Layers.Insert(Math.Min(_index, _doc.Layers.Count), _group);
    public void Undo() => _doc.Layers.Remove(_group);
}

/// <summary>Sets one layer's placement (position, size, rotation, flips), restoring the previous placement on undo.</summary>
public sealed class SetLayerTransformCommand : IUndoCommand
{
    private readonly Layer _layer;
    private readonly LayerTransform _oldTransform;
    private readonly LayerTransform _newTransform;

    public SetLayerTransformCommand(Layer layer, LayerTransform newTransform)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if (!newTransform.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(newTransform), "Transform is not valid.");
        }
        _layer = layer;
        _oldTransform = layer.Transform;
        _newTransform = newTransform;
    }

    public void Redo() => _layer.Transform = _newTransform;
    public void Undo() => _layer.Transform = _oldTransform;
}

/// <summary>
/// Bakes a horizontal (or vertical) mirror into one layer's pixels and toggles
/// its transform flip flags to match, restoring the previous surface on undo.
/// (Pixels are baked until transform-aware rendering lands; see PARITY.md.)
/// </summary>
public sealed class FlipLayerCommand : IUndoCommand
{
    private readonly Layer _layer;
    private readonly RasterSurface? _oldPixels;
    private readonly RasterSurface? _newPixels;
    private readonly LayerTransform _oldTransform;
    private readonly LayerTransform _newTransform;

    public FlipLayerCommand(Layer layer, bool horizontally)
    {
        ArgumentNullException.ThrowIfNull(layer);
        _layer = layer;
        _oldPixels = layer.Pixels;
        _newPixels = _oldPixels is null ? null : SurfaceOps.FlipCopy(_oldPixels, horizontally);
        _oldTransform = layer.Transform;
        _newTransform = horizontally
            ? _oldTransform with { FlipH = !_oldTransform.FlipH }
            : _oldTransform with { FlipV = !_oldTransform.FlipV };
    }

    public void Redo()
    {
        _layer.Pixels = _newPixels;
        _layer.Transform = _newTransform;
    }

    public void Undo()
    {
        _layer.Pixels = _oldPixels;
        _layer.Transform = _oldTransform;
    }
}

/// <summary>
/// Mirrors the whole canvas: every pixel layer bakes the flip (and toggles its
/// transform flags), every selection shape mirrors across the middle. One undo
/// step (upstream flipCanvas).
/// </summary>
public sealed class FlipCanvasCommand : IUndoCommand
{
    private sealed record FlipRecord(
        Layer Layer, RasterSurface? OldPixels, RasterSurface? NewPixels,
        LayerTransform OldTransform, LayerTransform NewTransform);

    private readonly Document _doc;
    private readonly bool _horizontally;
    private readonly IReadOnlyList<FlipRecord> _flips;
    private readonly DocumentSelection? _oldSelection;
    private readonly DocumentSelection? _newSelection;

    public FlipCanvasCommand(Document doc, bool horizontally)
    {
        ArgumentNullException.ThrowIfNull(doc);
        _doc = doc;
        _horizontally = horizontally;
        _oldSelection = doc.Selection;
        if (_oldSelection is { IsEmpty: false } sel)
        {
            _newSelection = sel.Shapes
                .Select(s => s.Mirrored(doc.Width, doc.Height, horizontally))
                .Aggregate(DocumentSelection.Empty(), (acc, shape) => DocumentSelection.ApplyTo(acc, shape));
        }

        var flips = new List<FlipRecord>();
        foreach (var layer in doc.Layers)
        {
            if (layer.Pixels is not { } surface)
            {
                continue;
            }
            var flipped = SurfaceOps.FlipCopy(surface, horizontally);
            var newT = horizontally
                ? layer.Transform with { FlipH = !layer.Transform.FlipH }
                : layer.Transform with { FlipV = !layer.Transform.FlipV };
            flips.Add(new FlipRecord(layer, surface, flipped, layer.Transform, newT));
        }
        _flips = flips;
    }

    public void Redo()
    {
        foreach (var flip in _flips)
        {
            flip.Layer.Pixels = flip.NewPixels;
            flip.Layer.Transform = flip.NewTransform;
        }
        _doc.Selection = _newSelection ?? _oldSelection;
    }

    public void Undo()
    {
        foreach (var flip in _flips)
        {
            flip.Layer.Pixels = flip.OldPixels;
            flip.Layer.Transform = flip.OldTransform;
        }
        _doc.Selection = _oldSelection;
    }
}
