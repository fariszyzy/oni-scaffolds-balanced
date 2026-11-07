using System;
using System.Collections.Generic;
using Klei.AI;
using UnityEngine;

namespace Scaffolds
{
  public static class ScaffoldNetworkManager
  {
    public const float DefaultUnstableDuration = 300f;
    public const float JiggleAmplitude = 0.03f;
    public const float HeavyLoadSpeedMultiplier = 0.25f;
    private const float FallbackCarryCapacity = 200f;

    public sealed class UnstableGroup
    {
      public readonly List<int> cells = new List<int>();
      public float timeRemaining = DefaultUnstableDuration;
      public bool collapsing;
      public readonly List<List<int>> waves = new List<List<int>>();
      public int nextWaveIndex;
    }

    private struct NavigatorCarryCache
    {
      public Navigator navigator;
      public Storage storage;
      public AttributeInstance carryAmount;
    }

    public static readonly Dictionary<int, Scaffold> ActiveScaffolds = new Dictionary<int, Scaffold>();
    public static readonly HashSet<int> DirtyCells = new HashSet<int>();

    private static readonly List<UnstableGroup> unstableGroups = new List<UnstableGroup>();
    private static readonly Stack<UnstableGroup> groupPool = new Stack<UnstableGroup>();
    private static readonly Stack<List<int>> waveListPool = new Stack<List<int>>();
    private static readonly Queue<int> bfsQueue = new Queue<int>();
    private static readonly HashSet<int> bfsVisited = new HashSet<int>();
    private static readonly List<int> bfsComponent = new List<int>();
    private static readonly List<int> dirtySnapshot = new List<int>();
    private static readonly List<int> waveSortBuffer = new List<int>();
    private static readonly Dictionary<int, NavigatorCarryCache> navigatorCarryCache = new Dictionary<int, NavigatorCarryCache>();

    private static readonly Comparison<int> CompareCellsTopToBottomDelegate = CompareCellsTopToBottom;
    private static readonly Action<int, object> OnGlobalSupportLayerChangedDelegate = OnGlobalSupportLayerChanged;

    private static ScaffoldNetworkTicker ticker;
    private static bool globalListenersRegistered;
    private static bool isTickingGroups;
    private static int tickCounter;

    public static void EnsureTicker(GameObject host)
    {
      if (ticker == null && host != null)
      {
        ticker = host.AddOrGet<ScaffoldNetworkTicker>();
      }
    }

    internal static void SetTicker(ScaffoldNetworkTicker instance)
    {
      ticker = instance;
    }

    public static bool IsSupportCell(int cell)
    {
      if (!Grid.IsValidCell(cell))
      {
        return false;
      }

      if (Grid.Solid[cell])
      {
        return true;
      }

      GameObject building = Grid.Objects[cell, (int)ObjectLayer.Building];
      if (building != null)
      {
        Ladder ladder = building.GetComponent<Ladder>();
        if (ladder != null && !(ladder is Scaffold))
        {
          return true;
        }
      }

      return false;
    }

    public static bool HasSupportForPlacement(int cell)
    {
      if (!Grid.IsValidCell(cell))
      {
        return false;
      }

      return IsPlacementNeighbourSupported(Grid.CellAbove(cell))
          || IsPlacementNeighbourSupported(Grid.CellBelow(cell))
          || IsPlacementNeighbourSupported(Grid.CellLeft(cell))
          || IsPlacementNeighbourSupported(Grid.CellRight(cell));
    }

    private static bool IsPlacementNeighbourSupported(int neighbourCell)
    {
      if (!Grid.IsValidCell(neighbourCell))
      {
        return false;
      }

      if (IsSupportCell(neighbourCell))
      {
        return true;
      }

      if (ActiveScaffolds.TryGetValue(neighbourCell, out Scaffold scaffold) && scaffold != null && scaffold.IsSupported)
      {
        return true;
      }

      return false;
    }

    public static void Register(Scaffold scaffold)
    {
      if (scaffold == null)
      {
        return;
      }

      if (Game.Instance != null)
      {
        EnsureTicker(Game.Instance.gameObject);
      }

      int cell = scaffold.Cell;
      if (!Grid.IsValidCell(cell))
      {
        return;
      }

      ActiveScaffolds[cell] = scaffold;
      scaffold.RefreshAnchor();

      if (scaffold.unstableTimeRemaining >= 0f && !scaffold.IsAnchor)
      {
        scaffold.IsSupported = false;
        scaffold.ApplyUnstableTint();
      }
      else
      {
        scaffold.IsSupported = true;
      }

      DirtyCells.Add(cell);
    }

    public static void Unregister(Scaffold scaffold)
    {
      if (scaffold == null)
      {
        return;
      }

      int cell = scaffold.Cell;
      if (Grid.IsValidCell(cell)
          && ActiveScaffolds.TryGetValue(cell, out Scaffold current)
          && current == scaffold)
      {
        ActiveScaffolds.Remove(cell);
      }

      UnstableGroup oldGroup = scaffold.Group;
      bool wasCollapsing = oldGroup != null && oldGroup.collapsing;

      if (oldGroup != null)
      {
        RemoveScaffoldFromGroup(scaffold, oldGroup);
        if (oldGroup.cells.Count == 0 && !isTickingGroups)
        {
          if (unstableGroups.Remove(oldGroup))
          {
            RecycleGroup(oldGroup);
          }
          SyncGlobalListeners();
        }
      }

      if (Grid.IsValidCell(cell))
      {
        MarkNeighbourDirtyOnDestroy(Grid.CellAbove(cell), oldGroup, wasCollapsing);
        MarkNeighbourDirtyOnDestroy(Grid.CellBelow(cell), oldGroup, wasCollapsing);
        MarkNeighbourDirtyOnDestroy(Grid.CellLeft(cell), oldGroup, wasCollapsing);
        MarkNeighbourDirtyOnDestroy(Grid.CellRight(cell), oldGroup, wasCollapsing);
      }
    }

    private static void MarkNeighbourDirtyOnDestroy(int neighbourCell, UnstableGroup destroyedGroup, bool wasCollapsing)
    {
      if (!Grid.IsValidCell(neighbourCell))
      {
        return;
      }

      if (ActiveScaffolds.TryGetValue(neighbourCell, out Scaffold neighbour) && neighbour != null)
      {
        if (wasCollapsing && neighbour.Group == destroyedGroup)
        {
          return;
        }
        if (neighbour.Group != null && neighbour.Group.collapsing)
        {
          return;
        }
        DirtyCells.Add(neighbourCell);
      }
    }

    public static void MarkDirty(int cell)
    {
      if (Grid.IsValidCell(cell))
      {
        DirtyCells.Add(cell);
      }
    }

    internal static void Tick(float dt)
    {
      if (DirtyCells.Count == 0 && unstableGroups.Count == 0)
      {
        return;
      }

      if (DirtyCells.Count > 0)
      {
        ProcessDirtyCells();
      }

      if (unstableGroups.Count > 0)
      {
        ProcessUnstableGroups(dt);
      }

      SyncGlobalListeners();
    }

    private static void ProcessDirtyCells()
    {
      dirtySnapshot.Clear();
      foreach (int cell in DirtyCells)
      {
        dirtySnapshot.Add(cell);
      }
      DirtyCells.Clear();
      bfsVisited.Clear();

      for (int i = 0; i < dirtySnapshot.Count; i++)
      {
        int startCell = dirtySnapshot[i];
        if (bfsVisited.Contains(startCell))
        {
          continue;
        }

        if (!ActiveScaffolds.TryGetValue(startCell, out Scaffold startScaffold) || startScaffold == null)
        {
          continue;
        }

        if (startScaffold.Group != null && startScaffold.Group.collapsing)
        {
          continue;
        }

        bfsQueue.Clear();
        bfsComponent.Clear();
        bfsVisited.Add(startCell);
        bfsQueue.Enqueue(startCell);
        bool hasAnchor = false;

        while (bfsQueue.Count > 0)
        {
          int currentCell = bfsQueue.Dequeue();
          if (!ActiveScaffolds.TryGetValue(currentCell, out Scaffold currentScaffold) || currentScaffold == null)
          {
            continue;
          }

          if (currentScaffold.Group != null && currentScaffold.Group.collapsing)
          {
            continue;
          }

          bfsComponent.Add(currentCell);
          currentScaffold.RefreshAnchor();
          if (currentScaffold.IsAnchor)
          {
            hasAnchor = true;
          }

          EnqueueNeighbour(Grid.CellAbove(currentCell));
          EnqueueNeighbour(Grid.CellBelow(currentCell));
          EnqueueNeighbour(Grid.CellLeft(currentCell));
          EnqueueNeighbour(Grid.CellRight(currentCell));
        }

        if (bfsComponent.Count == 0)
        {
          continue;
        }

        if (hasAnchor)
        {
          StabilizeComponent(bfsComponent);
        }
        else
        {
          MarkComponentUnstable(bfsComponent);
        }
      }

      bfsQueue.Clear();
      bfsVisited.Clear();
      bfsComponent.Clear();
      dirtySnapshot.Clear();
    }

    private static void EnqueueNeighbour(int neighbourCell)
    {
      if (!Grid.IsValidCell(neighbourCell) || bfsVisited.Contains(neighbourCell))
      {
        return;
      }

      if (ActiveScaffolds.TryGetValue(neighbourCell, out Scaffold neighbour) && neighbour != null)
      {
        if (neighbour.Group != null && neighbour.Group.collapsing)
        {
          return;
        }
        bfsVisited.Add(neighbourCell);
        bfsQueue.Enqueue(neighbourCell);
      }
    }

    private static void StabilizeComponent(List<int> component)
    {
      for (int i = 0; i < component.Count; i++)
      {
        int cell = component[i];
        if (!ActiveScaffolds.TryGetValue(cell, out Scaffold scaffold) || scaffold == null)
        {
          continue;
        }

        UnstableGroup oldGroup = scaffold.Group;
        if (oldGroup != null)
        {
          RemoveScaffoldFromGroup(scaffold, oldGroup);
          if (oldGroup.cells.Count == 0)
          {
            if (unstableGroups.Remove(oldGroup))
            {
              RecycleGroup(oldGroup);
            }
          }
        }

        if (!scaffold.IsSupported || scaffold.unstableTimeRemaining >= 0f || oldGroup != null)
        {
          scaffold.IsSupported = true;
          scaffold.unstableTimeRemaining = -1f;
          scaffold.ResetVisuals();
        }
      }
    }

    private static void MarkComponentUnstable(List<int> component)
    {
      float minTime = DefaultUnstableDuration;
      bool foundExistingTimer = false;

      for (int i = 0; i < component.Count; i++)
      {
        int cell = component[i];
        if (!ActiveScaffolds.TryGetValue(cell, out Scaffold scaffold) || scaffold == null)
        {
          continue;
        }

        UnstableGroup oldGroup = scaffold.Group;
        if (oldGroup != null)
        {
          if (!foundExistingTimer || oldGroup.timeRemaining < minTime)
          {
            minTime = oldGroup.timeRemaining;
            foundExistingTimer = true;
          }
          RemoveScaffoldFromGroup(scaffold, oldGroup);
          if (oldGroup.cells.Count == 0)
          {
            if (unstableGroups.Remove(oldGroup))
            {
              RecycleGroup(oldGroup);
            }
          }
        }

        if (scaffold.unstableTimeRemaining >= 0f && (!foundExistingTimer || scaffold.unstableTimeRemaining < minTime))
        {
          minTime = scaffold.unstableTimeRemaining;
          foundExistingTimer = true;
        }
      }

      UnstableGroup group = RentGroup(foundExistingTimer ? minTime : DefaultUnstableDuration);

      for (int i = 0; i < component.Count; i++)
      {
        int cell = component[i];
        if (!ActiveScaffolds.TryGetValue(cell, out Scaffold scaffold) || scaffold == null)
        {
          continue;
        }

        AddScaffoldToGroup(scaffold, group);
        scaffold.IsSupported = false;
        scaffold.unstableTimeRemaining = group.timeRemaining;
        scaffold.ApplyUnstableTint();
      }

      if (group.cells.Count > 0)
      {
        unstableGroups.Add(group);
      }
      else
      {
        RecycleGroup(group);
      }
    }

    private static UnstableGroup RentGroup(float timeRemaining)
    {
      UnstableGroup group = groupPool.Count > 0 ? groupPool.Pop() : new UnstableGroup();
      group.cells.Clear();
      group.waves.Clear();
      group.timeRemaining = timeRemaining;
      group.collapsing = false;
      group.nextWaveIndex = 0;
      return group;
    }

    private static void RecycleGroup(UnstableGroup group)
    {
      for (int i = 0; i < group.waves.Count; i++)
      {
        List<int> wave = group.waves[i];
        wave.Clear();
        waveListPool.Push(wave);
      }
      group.waves.Clear();
      group.cells.Clear();
      group.collapsing = false;
      group.timeRemaining = DefaultUnstableDuration;
      group.nextWaveIndex = 0;
      groupPool.Push(group);
    }

    private static void AddScaffoldToGroup(Scaffold scaffold, UnstableGroup group)
    {
      scaffold.Group = group;
      scaffold.GroupIndex = group.cells.Count;
      group.cells.Add(scaffold.Cell);
    }

    private static void RemoveScaffoldFromGroup(Scaffold scaffold, UnstableGroup group)
    {
      int index = scaffold.GroupIndex;
      int lastIndex = group.cells.Count - 1;
      if (index >= 0 && index <= lastIndex)
      {
        int lastCell = group.cells[lastIndex];
        group.cells[index] = lastCell;
        group.cells.RemoveAt(lastIndex);
        if (index < lastIndex && ActiveScaffolds.TryGetValue(lastCell, out Scaffold moved) && moved != null && moved.Group == group)
        {
          moved.GroupIndex = index;
        }
      }

      scaffold.Group = null;
      scaffold.GroupIndex = -1;
    }

    private static void ProcessUnstableGroups(float dt)
    {
      unchecked
      {
        tickCounter++;
      }

      isTickingGroups = true;
      try
      {
        for (int g = unstableGroups.Count - 1; g >= 0; g--)
        {
          UnstableGroup group = unstableGroups[g];
          if (group.cells.Count == 0 && !group.collapsing)
          {
            unstableGroups.RemoveAt(g);
            RecycleGroup(group);
            continue;
          }

          if (!group.collapsing)
          {
            group.timeRemaining -= dt;
            if (group.timeRemaining <= 0f)
            {
              StartCollapse(group);
            }
          }

          if (group.collapsing)
          {
            ProcessNextCollapseWave(group);

            if (group.nextWaveIndex >= group.waves.Count || group.cells.Count == 0)
            {
              for (int i = group.cells.Count - 1; i >= 0; i--)
              {
                int cell = group.cells[i];
                if (ActiveScaffolds.TryGetValue(cell, out Scaffold remaining) && remaining != null && remaining.Group == group)
                {
                  remaining.Group = null;
                  remaining.GroupIndex = -1;
                }
              }
              group.cells.Clear();
              unstableGroups.RemoveAt(g);
              RecycleGroup(group);
              continue;
            }
          }

          ApplyJiggleAndSyncTimer(group);
        }
      }
      finally
      {
        isTickingGroups = false;
      }
    }

    private static void ApplyJiggleAndSyncTimer(UnstableGroup group)
    {
      float timerValue = group.collapsing ? 0f : group.timeRemaining;

      for (int i = 0; i < group.cells.Count; i++)
      {
        int cell = group.cells[i];
        if (!ActiveScaffolds.TryGetValue(cell, out Scaffold scaffold) || scaffold == null)
        {
          continue;
        }

        scaffold.unstableTimeRemaining = timerValue;
        if (scaffold.animController != null)
        {
          Grid.CellToXY(cell, out int x, out int y);
          float dx = (((x + y + tickCounter) & 1) == 0) ? JiggleAmplitude : -JiggleAmplitude;
          scaffold.animController.Offset = new Vector3(dx, 0f, 0f);
        }
      }
    }

    private static void StartCollapse(UnstableGroup group)
    {
      group.collapsing = true;
      group.timeRemaining = 0f;
      for (int i = 0; i < group.waves.Count; i++)
      {
        List<int> oldWave = group.waves[i];
        oldWave.Clear();
        waveListPool.Push(oldWave);
      }
      group.waves.Clear();
      group.nextWaveIndex = 0;

      if (group.cells.Count == 0)
      {
        return;
      }

      waveSortBuffer.Clear();
      for (int i = 0; i < group.cells.Count; i++)
      {
        waveSortBuffer.Add(group.cells[i]);
      }

      waveSortBuffer.Sort(CompareCellsTopToBottomDelegate);

      List<int> currentWave = null;
      int currentRow = int.MinValue;

      for (int i = 0; i < waveSortBuffer.Count; i++)
      {
        int cell = waveSortBuffer[i];
        Grid.CellToXY(cell, out _, out int y);
        if (currentWave == null || y != currentRow)
        {
          currentRow = y;
          currentWave = waveListPool.Count > 0 ? waveListPool.Pop() : new List<int>();
          currentWave.Clear();
          group.waves.Add(currentWave);
        }
        currentWave.Add(cell);
      }

      waveSortBuffer.Clear();
    }

    private static int CompareCellsTopToBottom(int cellA, int cellB)
    {
      Grid.CellToXY(cellA, out int xA, out int yA);
      Grid.CellToXY(cellB, out int xB, out int yB);
      int rowCompare = yB.CompareTo(yA);
      return rowCompare != 0 ? rowCompare : xA.CompareTo(xB);
    }

    private static void ProcessNextCollapseWave(UnstableGroup group)
    {
      while (group.nextWaveIndex < group.waves.Count)
      {
        List<int> wave = group.waves[group.nextWaveIndex];
        group.nextWaveIndex++;
        bool deletedAny = false;

        for (int i = 0; i < wave.Count; i++)
        {
          int cell = wave[i];
          if (ActiveScaffolds.TryGetValue(cell, out Scaffold scaffold) && scaffold != null && scaffold.Group == group)
          {
            deletedAny = true;
            DeconstructableScaffold deconstructable = scaffold.GetComponent<DeconstructableScaffold>();
            if (deconstructable != null)
            {
              deconstructable.OnDeconstruct();
            }
            else
            {
              scaffold.gameObject.DeleteObject();
            }
          }
        }

        if (deletedAny)
        {
          break;
        }
      }
    }

    private static void SyncGlobalListeners()
    {
      bool shouldListen = unstableGroups.Count > 0;
      if (shouldListen && !globalListenersRegistered)
      {
        GameScenePartitioner partitioner = GameScenePartitioner.Instance;
        if (partitioner != null)
        {
          partitioner.AddGlobalLayerListener(partitioner.solidChangedLayer, OnGlobalSupportLayerChangedDelegate);
          partitioner.AddGlobalLayerListener(partitioner.objectLayers[(int)ObjectLayer.Building], OnGlobalSupportLayerChangedDelegate);
          globalListenersRegistered = true;
        }
      }
      else if (!shouldListen && globalListenersRegistered)
      {
        GameScenePartitioner partitioner = GameScenePartitioner.Instance;
        if (partitioner != null)
        {
          partitioner.RemoveGlobalLayerListener(partitioner.solidChangedLayer, OnGlobalSupportLayerChangedDelegate);
          partitioner.RemoveGlobalLayerListener(partitioner.objectLayers[(int)ObjectLayer.Building], OnGlobalSupportLayerChangedDelegate);
        }
        globalListenersRegistered = false;
      }
    }

    private static void OnGlobalSupportLayerChanged(int cell, object data)
    {
      if (!Grid.IsValidCell(cell))
      {
        return;
      }

      CheckUnstableNeighbour(Grid.CellAbove(cell));
      CheckUnstableNeighbour(Grid.CellBelow(cell));
      CheckUnstableNeighbour(Grid.CellLeft(cell));
      CheckUnstableNeighbour(Grid.CellRight(cell));
    }

    private static void CheckUnstableNeighbour(int neighbourCell)
    {
      if (!Grid.IsValidCell(neighbourCell))
      {
        return;
      }

      if (ActiveScaffolds.TryGetValue(neighbourCell, out Scaffold scaffold)
          && scaffold != null
          && scaffold.Group != null
          && !scaffold.Group.collapsing)
      {
        DirtyCells.Add(neighbourCell);
      }
    }

    public static float GetClimbSpeedMultiplier(Navigator navigator, Scaffold scaffold, int transitionY)
    {
      float multiplier = transitionY < 0
        ? scaffold.downwardsMovementSpeedMultiplier
        : scaffold.upwardsMovementSpeedMultiplier;

      int instanceId = navigator.GetInstanceID();
      if (!navigatorCarryCache.TryGetValue(instanceId, out NavigatorCarryCache cache) || cache.navigator != navigator)
      {
        cache.navigator = navigator;
        cache.storage = navigator.GetComponent<Storage>();
        Db db = Db.Get();
        cache.carryAmount = (db != null && db.Attributes != null && db.Attributes.CarryAmount != null)
          ? db.Attributes.CarryAmount.Lookup(navigator.gameObject)
          : null;
        navigatorCarryCache[instanceId] = cache;
      }

      if (cache.storage != null)
      {
        float carriedMass = cache.storage.MassStored();
        float capacity = cache.carryAmount != null ? cache.carryAmount.GetTotalValue() : FallbackCarryCapacity;
        if (capacity <= 0f)
        {
          capacity = FallbackCarryCapacity;
        }
        if (carriedMass >= capacity * 0.5f)
        {
          multiplier = HeavyLoadSpeedMultiplier;
        }
      }

      return multiplier;
    }

    public static void Clear()
    {
      if (globalListenersRegistered)
      {
        GameScenePartitioner partitioner = GameScenePartitioner.Instance;
        if (partitioner != null)
        {
          partitioner.RemoveGlobalLayerListener(partitioner.solidChangedLayer, OnGlobalSupportLayerChangedDelegate);
          partitioner.RemoveGlobalLayerListener(partitioner.objectLayers[(int)ObjectLayer.Building], OnGlobalSupportLayerChangedDelegate);
        }
        globalListenersRegistered = false;
      }

      foreach (KeyValuePair<int, Scaffold> kvp in ActiveScaffolds)
      {
        if (kvp.Value != null)
        {
          kvp.Value.Group = null;
          kvp.Value.GroupIndex = -1;
        }
      }

      ActiveScaffolds.Clear();
      DirtyCells.Clear();
      unstableGroups.Clear();
      groupPool.Clear();
      waveListPool.Clear();
      bfsQueue.Clear();
      bfsVisited.Clear();
      bfsComponent.Clear();
      dirtySnapshot.Clear();
      waveSortBuffer.Clear();
      navigatorCarryCache.Clear();

      ticker = null;
      isTickingGroups = false;
      tickCounter = 0;
    }
  }

  public sealed class ScaffoldNetworkTicker : KMonoBehaviour, ISim200ms
  {
    protected override void OnSpawn()
    {
      base.OnSpawn();
      ScaffoldNetworkManager.SetTicker(this);
    }

    public void Sim200ms(float dt)
    {
      ScaffoldNetworkManager.Tick(dt);
    }

    protected override void OnCleanUp()
    {
      ScaffoldNetworkManager.Clear();
      base.OnCleanUp();
    }
  }
}
