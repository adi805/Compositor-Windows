using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Compositor.Core;
using Compositor.Core.Commands;
using Compositor.Core.Project;
using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// Layer order is part of the file format, not a presentation detail. The format documents that a
/// parent precedes its children and the reader relies on it, so a list that violates the order
/// produces a project that saves and then cannot be reopened. These tests pin both halves: the
/// reorder keeps the invariant, and the write path refuses to break it.
/// </summary>
public sealed class LayerOrderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"layer-order-{Guid.NewGuid():N}");

    public LayerOrderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string PathFor(string name) => Path.Combine(_dir, name);

    // ----- reordering a group -----

    [Fact]
    public void Reorder_GroupTravelsWithItsDescendants()
    {
        var doc = new Document(8, 8);
        var group = Layer.Group("g");
        var childA = new Layer("a") { ParentId = group.Id };
        var childB = new Layer("b") { ParentId = group.Id };
        var other = new Layer("o");
        doc.Layers.AddRange([group, childA, childB, other]);

        new ReorderLayerCommand(doc, group, 3).Redo();

        // The group moved, its two children moved with it, and the block kept its internal order.
        Assert.Equal(["o", "g", "a", "b"], Names(doc));
        LayerHierarchy.Validate(doc.Layers);
    }

    [Fact]
    public void Reorder_GroupToTheBottom_KeepsTheBlockIntact()
    {
        // The target index is inside the block's own span, which is the case that a naive
        // remove-then-insert gets wrong: after the members leave, index 0 is the only valid target.
        var doc = new Document(8, 8);
        var other = new Layer("o");
        var group = Layer.Group("g");
        var child = new Layer("a") { ParentId = group.Id };
        doc.Layers.AddRange([other, group, child]);

        new ReorderLayerCommand(doc, group, 0).Redo();

        Assert.Equal(["g", "a", "o"], Names(doc));
        LayerHierarchy.Validate(doc.Layers);
    }

    [Fact]
    public void Reorder_GroupUndo_RestoresTheExactOriginalOrder()
    {
        var doc = new Document(8, 8);
        var group = Layer.Group("g");
        var child = new Layer("a") { ParentId = group.Id };
        var other = new Layer("o");
        doc.Layers.AddRange([group, child, other]);
        var before = doc.Layers.ToArray();

        var command = new ReorderLayerCommand(doc, group, 2);
        command.Redo();
        command.Undo();

        Assert.Equal(before, doc.Layers);
    }

    [Fact]
    public void Reorder_SingleLayer_StillMovesExactlyOneEntry()
    {
        // The plain case the command has always handled: a layer with no descendants is a
        // one-element block, so the block logic must not change what it does.
        var doc = new Document(10, 10);
        var a = new Layer("a");
        var b = new Layer("b");
        var c = new Layer("c");
        doc.Layers.AddRange([a, b, c]);

        new ReorderLayerCommand(doc, a, 2).Redo();

        Assert.Equal(["b", "c", "a"], Names(doc));
    }

    [Fact]
    public void Reorder_GroupPastItsOwnChild_LeavesAFileThatReopens()
    {
        // The reproduction from the issue, end to end: three layers, drag the group past its child,
        // save, reopen. Before the fix the save succeeded and the load failed with a missing parent.
        var doc = new Document(8, 8);
        var group = Layer.Group("g");
        var child = new Layer("c") { ParentId = group.Id };
        var other = new Layer("o");
        doc.Layers.AddRange([group, child, other]);

        new ReorderLayerCommand(doc, group, 2).Redo();

        var path = PathFor("reordered.comp");
        ProjectStore.Save(doc, path);
        var loaded = ProjectStore.Load(path);

        Assert.Equal(["o", "g", "c"], Names(doc));
        Assert.Equal(["o", "g", "c"], loaded.Layers.Select(l => l.Name));
        Assert.Equal(group.Id, loaded.Layers.Single(l => l.Name == "c").ParentId);
    }

    // ----- the write path refuses an unreopenable hierarchy -----

    [Fact]
    public void Save_RejectsAChildBeforeItsParent()
    {
        // Built by hand because the commands no longer produce this shape: the point is that the
        // format boundary refuses it regardless of how the document was assembled.
        var doc = new Document(8, 8);
        var group = Layer.Group("g");
        var child = new Layer("c") { ParentId = group.Id };
        doc.Layers.AddRange([child, group]);

        var error = Assert.Throws<InvalidOperationException>(() => ProjectStore.Save(doc, PathFor("bad.comp")));

        Assert.Contains("before its parent", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(PathFor("bad.comp")), "a refused save must not leave a file behind");
    }

    [Fact]
    public void Save_RejectsACycle()
    {
        var doc = new Document(8, 8);
        var a = Layer.Group("a");
        var b = Layer.Group("b");
        a.ParentId = b.Id;
        b.ParentId = a.Id;
        doc.Layers.AddRange([a, b]);

        var error = Assert.Throws<InvalidOperationException>(() => ProjectStore.Save(doc, PathFor("cycle.comp")));

        Assert.Contains("Cycle", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_RejectsADanglingParent()
    {
        var doc = new Document(8, 8);
        doc.AddLayer(new Layer("orphan") { ParentId = Guid.NewGuid() });

        var error = Assert.Throws<InvalidOperationException>(() => ProjectStore.Save(doc, PathFor("orphan.comp")));

        Assert.Contains("missing parent", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_RefusalLeavesTheExistingFileUntouched()
    {
        // Why validating before the write matters: the failure has to leave the good file that is
        // already on disk exactly as it was, not replace it with something that cannot be opened.
        var doc = new Document(8, 8);
        doc.AddLayer(new Layer("fine"));
        var path = PathFor("existing.comp");
        ProjectStore.Save(doc, path);
        var before = File.ReadAllBytes(path);

        var broken = new Document(8, 8);
        var group = Layer.Group("g");
        var child = new Layer("c") { ParentId = group.Id };
        broken.Layers.AddRange([child, group]);

        Assert.Throws<InvalidOperationException>(() => ProjectStore.Save(broken, path));

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal("fine", ProjectStore.Load(path).Layers.Single().Name);
    }

    // ----- the read path repairs a non-canonical order -----

    [Fact]
    public void Load_NormalizesAChildBeforeItsParent()
    {
        // A manifest written by something other than this app, or by an older build. The references
        // are all present, only the order is wrong, so the file is readable and the reader puts it
        // into the order the format documents instead of refusing it.
        var path = PathFor("noncanonical.comp");
        var groupId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        WriteManifest(path, ManifestWithLayers(
            new { uuid = childId, name = "c", parentUUID = groupId },
            new { uuid = groupId, name = "g", isGroup = true }));

        var doc = ProjectStore.Load(path);

        Assert.Equal(["g", "c"], doc.Layers.Select(l => l.Name));
        Assert.Equal(groupId, doc.Layers[1].ParentId);
    }

    [Fact]
    public void Load_StillRejectsAnActuallyMissingParent()
    {
        // The boundary of the repair: order can be fixed by reordering, a reference to a layer that
        // is not in the file cannot.
        var path = PathFor("dangling.comp");
        WriteManifest(path, ManifestWithLayers(
            new { uuid = Guid.NewGuid(), name = "c", parentUUID = Guid.NewGuid() }));

        var error = Assert.Throws<InvalidOperationException>(() => ProjectStore.Load(path));

        Assert.Contains("missing parent", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_StillRejectsACycle()
    {
        var path = PathFor("load-cycle.comp");
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        WriteManifest(path, ManifestWithLayers(
            new { uuid = a, name = "a", isGroup = true, parentUUID = b },
            new { uuid = b, name = "b", isGroup = true, parentUUID = a }));

        var error = Assert.Throws<InvalidOperationException>(() => ProjectStore.Load(path));

        Assert.Contains("Cycle", error.Message, StringComparison.Ordinal);
    }

    // ----- Normalize on its own -----

    [Fact]
    public void Normalize_LeavesACanonicalListUntouched()
    {
        var doc = new Document(8, 8);
        var group = Layer.Group("g");
        var child = new Layer("c") { ParentId = group.Id };
        var other = new Layer("o");
        doc.Layers.AddRange([group, child, other]);
        var before = doc.Layers.ToArray();

        var normalized = LayerHierarchy.Normalize(doc.Layers);

        Assert.Equal(before, normalized);
    }

    [Fact]
    public void Normalize_TerminatesOnACycle()
    {
        // The traversal must not hang on the malformed input it exists to help diagnose: a layer is
        // emitted at most once, so a parent that reaches itself stops rather than recursing forever.
        var a = Layer.Group("a");
        var b = Layer.Group("b");
        a.ParentId = b.Id;
        b.ParentId = a.Id;

        var normalized = LayerHierarchy.Normalize([a, b]);

        Assert.Equal(2, normalized.Count);
        Assert.Contains(a, normalized);
        Assert.Contains(b, normalized);
    }

    private static List<string> Names(Document doc) => doc.Layers.Select(l => l.Name).ToList();

    /// <summary>
    /// A minimal valid manifest whose layers are supplied verbatim, so a test can hand the reader
    /// a shape <see cref="ProjectStore.Save"/> refuses to produce (out-of-order parents, cycles,
    /// dangling references). Every layer gets a canvas-sized transform because the reader requires
    /// one to be present.
    /// </summary>
    private static string ManifestWithLayers(params object[] layers) =>
        JsonSerializer.Serialize(new
        {
            identifier = ProjectStore.Identifier,
            version = ProjectStore.Version,
            documentUUID = Guid.NewGuid().ToString(),
            name = "Layer order fixture",
            width = 8,
            height = 8,
            layers = layers.Select(l => Merge(l)).ToArray(),
        });

    private static Dictionary<string, object?> Merge(object layer)
    {
        var merged = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["transform"] = new { width = 8, height = 8 },
        };
        foreach (var property in layer.GetType().GetProperties())
        {
            merged[property.Name] = property.GetValue(layer);
        }

        return merged;
    }

    private static void WriteManifest(string path, string manifest)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        using var entry = zip.CreateEntry("manifest.json", CompressionLevel.NoCompression).Open();
        entry.Write(Encoding.UTF8.GetBytes(manifest));
    }
}
