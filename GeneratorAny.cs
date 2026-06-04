using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Fakutori.Grid;
using FakutoriCustom;
using HarmonyLib;
using Sirenix.Serialization;
using UnityEngine;
using UnityEngine.Events;

public class GeneratorAny : ProcessingBlock
{

    public override int actionPhaseOrder => 1001;

    private const int toggleDuration = 4;

    public const int frequency = 2;

    // are those even used?

    public BlockVisuals blockVisuals;

    public GeneratorAny()
    {

    }

    private void ResetIcon()
    {
        try 
        {
            var go = blockVisuals.transform.Find("Visuals").Find("Block");
            var ren = go.GetComponent<SpriteRenderer>();
            var mpb = new MaterialPropertyBlock();
            ren.GetPropertyBlock(mpb);
            mpb.SetTexture("_FXTexture", Plugin.GeneratorAnyFxTexture);
            ren.SetPropertyBlock(mpb);
        } catch (Exception e) 
        {
            // oops
        }
        // Why no working either?
    }

    public override void ExecuteActionPhase()
    {
        if (!UpdateProcess())
        {
            return;
        }
        base.outputBlocks.Clear();

        // if ((AbstractSingleton<TimeManager>.Instance.stepIndex) % 2 == 1)
        // if ((AbstractSingleton<TimeManager>.Instance.stepIndex) % 4 == 1)
        // if ((AbstractSingleton<TimeManager>.Instance.stepIndex + 32) % 16 == 0)
        if (true)
        {
            ResetIcon();
            GridCell cellInDirection = base.gridCell.GetCellInDirection(base.direction);
            if (cellInDirection.IsCellFree(base.layer))
            {
                BlockData GeneratedBlock = GetRandomUnlockedBlock();
                int num = (base.stepsDuration = 4);
                base.stepsRemaining = num;
                base.outputBlocks = new List<ElementBlock> { AbstractSingleton<BlocksManager>.Instance.SpawnBlock(cellInDirection, GeneratedBlock, 4, ignoreBlockOnPosition: false, AbstractSingleton<BlocksManager>.Instance.GetNewBlockStatus(GeneratedBlock)) as ElementBlock };
                base.outputBlocks[0].onInterruptAction.AddListener(InterruptAction);
                UnityEvent<BlockAction> onAction = base.OnAction;
                Block[] array = base.outputBlocks.ToArray();
                onAction.Invoke(new BlockAction(BlockActionType.Success, 4, 0, null, array));
            }
            else
            {
                base.OnAction.Invoke(new BlockAction(BlockActionType.Failure, 0));
            }
        }
        // Plugin.Logger.LogInfo("End of GeneratorAny ExecuteActionPhase");
    }

    private BlockData GetRandomUnlockedBlock()
    {
        // Initialize with the four ElementBlocks that have UnlockedByDefault set to true
        BlocksLibrary blocksLibrary = Resources.FindObjectsOfTypeAll<BlocksLibrary>()[0];
        var ElementBlocksField = AccessTools.Field(typeof(BlocksLibrary), "ElementBlocks");
        BlockData[] elementBlocks = (BlockData[])ElementBlocksField.GetValue(blocksLibrary);
        List<BlockData> list = elementBlocks
            .Where(block => block.unlockedByDefault)
            .Where(block => block.category && block.category.showInCompendium)
        .ToList();

        // Quadruple the odds:
        list.AddRange(list);
        list.AddRange(list);

        // Add in any block with completed challenges
        ProgressManager progressManager = AbstractSingleton<ProgressManager>.Instance;
        list.AddRange(
            elementBlocks
            .Where(block => !block.unlockedByDefault)
            .Where(block => progressManager.GetProgress(block).isChallengeCompleted)
            .ToList()
        );

        return list[UnityEngine.Random.Range(0, list.Count)];
    }

    protected override List<ElementBlock> GetOutputBlocks()
    {
        ElementBlock elementBlock = base.gridCell?.GetElementBlockInNeighbor(base.direction);
        if (elementBlock != null)
        {
            return new List<ElementBlock> { elementBlock };
        }
        return new List<ElementBlock>();
    }

    public override SerializedBlock SerializeBlock(bool saveAsBlueprint)
    {
        return new SerializedProcessingBlock(base.SerializeBlock(saveAsBlueprint))
        {
            Duration = ((!saveAsBlueprint) ? base.stepsDuration : 0),
            ElapsedSteps = ((!saveAsBlueprint) ? (4 - base.stepsRemaining) : 0)
        };
    }

    public override void DeserializeBlock(SerializedBlock block, BlockData data)
    {
        base.DeserializeBlock(block, data);
        SerializedProcessingBlock serializedProcessingBlock = block as SerializedProcessingBlock;
        base.stepsDuration = serializedProcessingBlock.Duration;
        base.stepsRemaining = base.stepsDuration - serializedProcessingBlock.ElapsedSteps;
        if (base.stepsRemaining > 0)
        {
            base.outputBlocks = GetOutputBlocks();
            if (base.outputBlocks.Count == 1)
            {
                base.outputBlocks[0].onInterruptAction.AddListener(InterruptAction);
                UnityEvent<BlockAction> onAction = base.OnAction;
                int num = base.stepsDuration;
                int num2 = 4 - base.stepsRemaining;
                Block[] array = base.outputBlocks.ToArray();
                onAction.Invoke(new BlockAction(BlockActionType.Success, num, num2, null, array));
            }
        }
    }

    public override void DeserializeBlockOnClone(SerializedBlock block, BlockData data)
    {
        base.DeserializeBlockOnClone(block, data);
        int num = (base.stepsRemaining = 0);
        base.stepsDuration = num;
    }

    public override Block GetNewInstance()
    {
        return new GeneratorAny();
    }

    public override bool isPermanent
    {
        get
        {
            return true;
        }
    }

    public override bool canBeSoldForMoney
    {
        get
        {
            return false;
        }
    }

    public override bool Despawn()
    {
        return !this.isPermanent && base.Despawn();
    }
}
