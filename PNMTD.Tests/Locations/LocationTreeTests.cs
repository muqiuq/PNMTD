using PNMTD.Locations;

namespace PNMTD.Tests.Locations;

/// <summary>
/// The tree in memory (docs/locations-knowledge-plan.md §3.3): paths, ancestors and descendants, depths, and where a
/// location may go: never under itself or its own sub-locations, never past 6 levels with its subtree.
/// </summary>
[TestClass]
public sealed class LocationTreeTests
{
    private static readonly Guid Site = Guid.NewGuid(), BuildingA = Guid.NewGuid(), Room = Guid.NewGuid(), CageB = Guid.NewGuid(),
        Office = Guid.NewGuid(), Other = Guid.NewGuid();

    // Zurich DC › Building A › Room 2.13 › Cage B; Zurich DC › Office; Bern.
    private static LocationTreeSnapshot Tree() => LocationTreeSnapshot.From(
    [
        new LocationNode(Room, BuildingA, "Room 2.13"),
        new LocationNode(Site, null, "Zurich DC"),
        new LocationNode(CageB, Room, "Cage B"),
        new LocationNode(BuildingA, Site, "Building A"),
        new LocationNode(Office, Site, "office"),
        new LocationNode(Other, null, "Bern"),
    ]);

    [TestMethod]
    public void PathsAncestorsAndDepthsComeFromTheParents()
    {
        var tree = Tree();

        Assert.AreEqual("Zurich DC › Building A › Room 2.13 › Cage B", tree.Path(CageB));
        CollectionAssert.AreEqual(new[] { Room, BuildingA, Site }, tree.Ancestors(CageB).Select(a => a.Id).ToArray());
        CollectionAssert.AreEqual(new[] { Site, BuildingA, Room, CageB }, tree.Lineage(CageB).Select(a => a.Id).ToArray());
        Assert.AreEqual(4, tree.Depth(CageB));
        Assert.AreEqual(1, tree.Depth(Site));
        Assert.AreEqual(0, tree.Depth(Guid.NewGuid()));
        Assert.AreEqual("", tree.Path(Guid.NewGuid()));
        Assert.AreEqual(0, tree.Ancestors(Site).Count);
    }

    [TestMethod]
    public void ChildrenAndDescendantsAreByNameDepthFirst()
    {
        var tree = Tree();

        CollectionAssert.AreEqual(new[] { "Bern", "Zurich DC" }, tree.Roots.Select(r => r.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "Building A", "office" }, tree.Children(Site).Select(c => c.Name).ToArray());
        CollectionAssert.AreEqual(new[] { BuildingA, Room, CageB, Office }, tree.Descendants(Site).ToArray());
        CollectionAssert.AreEqual(new[] { Room, CageB }, tree.SelfAndDescendants(Room).ToArray());
        Assert.AreEqual(0, tree.SelfAndDescendants(Guid.NewGuid()).Count);
        Assert.AreEqual(4, tree.Height(Site));
        Assert.AreEqual(1, tree.Height(CageB));
        Assert.IsTrue(tree.IsWithin(CageB, Site));
        Assert.IsTrue(tree.IsWithin(Site, Site));
        Assert.IsFalse(tree.IsWithin(Site, CageB));
        Assert.IsFalse(tree.IsWithin(Other, Site));
    }

    [TestMethod]
    public void ALocationCantGoUnderItselfOrItsOwnSubLocations()
    {
        var tree = Tree();

        Assert.AreEqual(LocationErrors.Cycle, tree.PlacementProblem(Site, Site));
        Assert.AreEqual(LocationErrors.Cycle, tree.PlacementProblem(Site, CageB));
        Assert.AreEqual(LocationErrors.Cycle, tree.PlacementProblem(BuildingA, Room));
        Assert.IsNull(tree.PlacementProblem(Room, Office));
        Assert.IsNull(tree.PlacementProblem(Room, null));
        Assert.IsNull(tree.PlacementProblem(Other, CageB));
    }

    [TestMethod]
    public void SixLevelsAtMostWithTheSubtree()
    {
        // A chain of six: L1 › L2 › … › L6.
        var ids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToList();
        var nodes = ids.Select((id, i) => new LocationNode(id, i == 0 ? null : ids[i - 1], $"L{i + 1}")).ToList();
        var loose = Guid.NewGuid();
        var pair = (Top: Guid.NewGuid(), Below: Guid.NewGuid());
        nodes.Add(new LocationNode(loose, null, "Loose"));
        nodes.Add(new LocationNode(pair.Top, null, "Pair"));
        nodes.Add(new LocationNode(pair.Below, pair.Top, "Below"));
        var tree = LocationTreeSnapshot.From(nodes);

        Assert.AreEqual(6, tree.Depth(ids[5]));
        // A seventh level is refused, for a new location as for a move.
        Assert.AreEqual(LocationErrors.TooDeep, tree.PlacementProblem(Guid.NewGuid(), ids[5]));
        Assert.AreEqual(LocationErrors.TooDeep, tree.PlacementProblem(loose, ids[5]));
        Assert.IsNull(tree.PlacementProblem(loose, ids[4]));
        // A location with a sub-location needs room for both.
        Assert.AreEqual(LocationErrors.TooDeep, tree.PlacementProblem(pair.Top, ids[4]));
        Assert.IsNull(tree.PlacementProblem(pair.Top, ids[3]));
    }

    [TestMethod]
    public void ACycleInTheDataEndsAWalk()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var tree = LocationTreeSnapshot.From([new LocationNode(a, b, "A"), new LocationNode(b, a, "B")]);

        Assert.AreEqual(1, tree.Ancestors(a).Count);
        Assert.AreEqual(1, tree.Descendants(a).Count);
        Assert.AreEqual(0, tree.Roots.Count);
    }
}
