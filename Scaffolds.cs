using KSerialization;
using System;
using UnityEngine;

namespace Scaffolds
{
  public class Scaffold : Ladder
  {
    private static readonly Action<object> DeconstructCallback = Deconstruct;
    private static readonly Color32 NormalTint = new Color32(255, 255, 255, 255);
    private static readonly Color32 UnstableTint = new Color32(255, 170, 120, 255);

    [Serialize]
    private float deconstructMoment = -1f;

    [Serialize]
    internal float unstableTimeRemaining = -1f;

    public SchedulerHandle deconstructHandle;

    public bool IsSupported = true;

    public int Cell { get; private set; } = Grid.InvalidCell;

    public bool IsAnchor { get; private set; }

    internal KBatchedAnimController animController;
    internal ScaffoldNetworkManager.UnstableGroup Group;
    internal int GroupIndex = -1;

    private HandleVector<int>.Handle[] anchorHandles;
    private int anchorHandleCount;
    private Action<object> onSupportCellChangedDelegate;
    private bool registeredInNetwork;
    private bool cleanedUp;

    protected override void OnPrefabInit()
    {
      base.OnPrefabInit();

#if DEBUG
      Debug.Log("[Scaffolds] Scaffold OnPrefabInit");
#endif
    }

    protected override void OnSpawn()
    {
      base.OnSpawn();

#if DEBUG
      Debug.Log("[Scaffolds] Scaffold OnSpawn");
#endif
      registeredInNetwork = true;
      cleanedUp = false;
      Cell = Grid.PosToCell(this);
      animController = GetComponent<KBatchedAnimController>();

      float now = GameClock.Instance.GetTime();
      float timeRemaining = deconstructMoment < 0f
        ? Mathf.Max(0.1f, Scaffolds_Patch.Settings.Duration * 600.0f)
        : Mathf.Max(0.1f, deconstructMoment - now);
#if DEBUG
      Debug.Log($"[Scaffolds] Scheduling deconstruct in {timeRemaining} seconds");
#endif
      this.scheduleDeconstruct(timeRemaining);

      ScaffoldNetworkManager.Register(this);
    }

    public void RefreshAnchor()
    {
      FreeAnchorHandles();
      IsAnchor = false;

      if (cleanedUp || !Grid.IsValidCell(Cell))
      {
        return;
      }

      CheckAndRegisterAnchorNeighbour(Grid.CellAbove(Cell));
      CheckAndRegisterAnchorNeighbour(Grid.CellBelow(Cell));
      CheckAndRegisterAnchorNeighbour(Grid.CellLeft(Cell));
      CheckAndRegisterAnchorNeighbour(Grid.CellRight(Cell));
    }

    private void CheckAndRegisterAnchorNeighbour(int neighbourCell)
    {
      if (!Grid.IsValidCell(neighbourCell) || !ScaffoldNetworkManager.IsSupportCell(neighbourCell))
      {
        return;
      }

      IsAnchor = true;

      GameScenePartitioner partitioner = GameScenePartitioner.Instance;
      if (partitioner == null)
      {
        return;
      }

      if (anchorHandles == null)
      {
        anchorHandles = new HandleVector<int>.Handle[8];
      }
      if (onSupportCellChangedDelegate == null)
      {
        onSupportCellChangedDelegate = OnSupportCellChanged;
      }

      if (anchorHandleCount + 2 <= anchorHandles.Length)
      {
        anchorHandles[anchorHandleCount++] = partitioner.Add(
          "Scaffold.Solid",
          this,
          neighbourCell,
          partitioner.solidChangedLayer,
          onSupportCellChangedDelegate);
        anchorHandles[anchorHandleCount++] = partitioner.Add(
          "Scaffold.Building",
          this,
          neighbourCell,
          partitioner.objectLayers[(int)ObjectLayer.Building],
          onSupportCellChangedDelegate);
      }
    }

    private void OnSupportCellChanged(object data)
    {
      if (cleanedUp)
      {
        return;
      }

      // Never add/free partitioner entries while the partitioner is dispatching this event.
      // The batched BFS in ScaffoldNetworkManager re-evaluates (RefreshAnchor) every node it visits.
      ScaffoldNetworkManager.MarkDirty(Cell);
    }

    private void FreeAnchorHandles()
    {
      if (anchorHandles == null || anchorHandleCount == 0)
      {
        anchorHandleCount = 0;
        return;
      }

      GameScenePartitioner partitioner = GameScenePartitioner.Instance;
      for (int i = 0; i < anchorHandleCount; i++)
      {
        if (partitioner != null && anchorHandles[i].IsValid())
        {
          partitioner.Free(ref anchorHandles[i]);
        }
        anchorHandles[i] = default;
      }
      anchorHandleCount = 0;
    }

    public void ApplyUnstableTint()
    {
      if (animController != null)
      {
        animController.TintColour = UnstableTint;
      }
    }

    public void ResetVisuals()
    {
      if (animController != null)
      {
        animController.Offset = Vector3.zero;
        animController.TintColour = NormalTint;
      }
    }

    private void scheduleDeconstruct(float timeRemaining)
    {
      if (deconstructHandle.IsValid)
      {
#if DEBUG
        Debug.Log("[Scaffolds] Clearing previous deconstruct schedule");
#endif
        deconstructHandle.ClearScheduler();
      }
      deconstructHandle = GameScheduler.Instance.Schedule(nameof(Scaffold), timeRemaining, DeconstructCallback, this);

      deconstructMoment = GameClock.Instance.GetTime() + timeRemaining;
#if DEBUG
      Debug.Log($"[Scaffolds] Scheduled deconstruct for {deconstructMoment}");
#endif
    }

    private static void Deconstruct(object data)
    {
      Scaffold scaffold = data as Scaffold;
      if (scaffold != null)
      {
#if DEBUG
        Debug.Log("[Scaffolds] Timer-triggered Scaffold Deconstruct");
#endif
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
      else
      {
        Debug.LogError("[Scaffolds] Scaffolds attempted to automatically deconstruct something that wasn't a Scaffold or was null. Please report this error.");
      }
    }

    protected override void OnCleanUp()
    {
      cleanedUp = true;
      if (deconstructHandle.IsValid)
      {
        this.deconstructHandle.ClearScheduler();
      }
      FreeAnchorHandles();
      if (registeredInNetwork)
      {
        registeredInNetwork = false;
        ScaffoldNetworkManager.Unregister(this);
      }
      base.OnCleanUp();
#if DEBUG
      Debug.Log("[Scaffolds] Scaffold finished OnCleanUp");
#endif
    }
  }

  public class DeconstructableScaffold : Workable
  {
    // Modified deconstructable to replace the default behavior, this one will deconstruct instantly when given deconstruct order
    // However, it won't drop any resources from the building itself. This is important because it's made of vacuum and would give an error

    private static readonly EventSystem.IntraObjectHandler<DeconstructableScaffold> OnRefreshUserMenuDelegate = new EventSystem.IntraObjectHandler<DeconstructableScaffold>(delegate (DeconstructableScaffold component, object data)
    {
      component.OnRefreshUserMenu(data);
    });
    private static readonly EventSystem.IntraObjectHandler<DeconstructableScaffold> OnDeconstructDelegate = new EventSystem.IntraObjectHandler<DeconstructableScaffold>(delegate (DeconstructableScaffold component, object data)
    {
      component.OnDeconstruct();
    });
    private CellOffset[] PlacementOffsets
    {
      get
      {
        Building component = GetComponent<Building>();
        if (component != null)
        {
          return component.Def.PlacementOffsets;
        }

        Debug.LogError("[Scaffolds] There's an error with the Scaffolds mod deconstructing. Please report this error.");
        return null;
      }
    }

    protected override void OnPrefabInit()
    {
      base.OnPrefabInit();
      Subscribe(493375141, OnRefreshUserMenuDelegate);
      Subscribe(-111137758, OnRefreshUserMenuDelegate);
      Subscribe(-790448070, OnDeconstructDelegate);

      CellOffset[][] table = OffsetGroups.InvertedStandardTable;
      CellOffset[] filter = null;
      CellOffset[][] offsetTable = OffsetGroups.BuildReachabilityTable(PlacementOffsets, table, filter);
      SetOffsetTable(offsetTable);
      // Not really sure exactly what the CellOffset stuff is about, too afraid to delete
      // from original deconstructable class
    }

    protected override void OnSpawn()
    {
      base.OnSpawn();
    }

    public void OnDeconstruct()
    {
      base.gameObject.DeleteObject(); // Goodbye
    }

    private void OnRefreshUserMenu(object data)
    {
      // Add deconstruct button
      KIconButtonMenu.ButtonInfo button = new KIconButtonMenu.ButtonInfo("action_deconstruct", ScaffoldConfig.DeconstructButtonText, OnDeconstruct, Action.NumActions, null, null, null, ScaffoldConfig.DeconstructButtonTooltip);
      Game.Instance.userMenu.AddButton(base.gameObject, button, 0f);
#if DEBUG
      Debug.Log("[Scaffolds] Deconstruct button added to user menu during OnRefreshUserMenu.");
#endif
    }
  }
}
