using OneDriveOrganizer.Models;

namespace OneDriveOrganizer.Services.AI;

public static class CosineGrouping
{
    private sealed record Node(int Index, double Radius, Node? Inner, Node? Outer);
    public static void Merge(List<FileRecord> files, IReadOnlyDictionary<string, float[]> vectors, UnionFind union, double threshold, CancellationToken token)
    {
        if (!double.IsFinite(threshold) || threshold is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(threshold));
        var indices = Enumerable.Range(0, files.Count).Where(i => vectors.ContainsKey(files[i].Path)).ToArray();
        // A metric tree preserves recall; high-dimensional/dense data can still approach quadratic work.
        double Distance(int a, int b) => Math.Sqrt(Math.Max(0, 2 - 2 * Vectors.Cosine(vectors[files[a].Path], vectors[files[b].Path])));
        Node? Build(int[] items)
        {
            token.ThrowIfCancellationRequested();
            if (items.Length == 0) return null;
            int pivot = items[0];
            var rest = items.Skip(1).Select(i => (Index: i, Distance: Distance(pivot, i))).OrderBy(x => x.Distance).ToArray();
            int middle = rest.Length / 2;
            return new Node(pivot, rest.Length == 0 ? 0 : rest[middle].Distance, Build(rest.Take(middle).Select(x => x.Index).ToArray()), Build(rest.Skip(middle).Select(x => x.Index).ToArray()));
        }
        var root = Build(indices);
        double radius = Math.Sqrt(2 - 2 * threshold);
        void Visit(Node? node, int index)
        {
            if (node == null) return;
            token.ThrowIfCancellationRequested();
            double distance = Distance(index, node.Index);
            if (node.Index < index && distance <= radius + 1e-12 && Vectors.Cosine(vectors[files[index].Path], vectors[files[node.Index].Path]) >= threshold) union.Join(index, node.Index);
            if (distance - radius <= node.Radius + 1e-12) Visit(node.Inner, index);
            if (distance + radius >= node.Radius - 1e-12) Visit(node.Outer, index);
        }
        foreach (int i in indices) Visit(root, i);
    }
}
