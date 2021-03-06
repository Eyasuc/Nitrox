﻿using NitroxClient.Communication.Abstract;
using NitroxClient.GameLogic.Bases;
using NitroxClient.GameLogic.Helper;
using NitroxClient.MonoBehaviours.Overrides;
using NitroxClient.Unity.Helper;
using NitroxModel.Core;
using NitroxModel.DataStructures.GameLogic;
using NitroxModel.DataStructures.Util;
using NitroxModel.Helper;
using NitroxModel.Logger;
using NitroxModel.Packets;
using System;
using System.Reflection;
using UnityEngine;
using static NitroxClient.GameLogic.Helper.TransientLocalObjectManager;
using NitroxModel_Subnautica.Helper;
using NitroxModel.DataStructures;

namespace NitroxClient.MonoBehaviours
{
    /**
     * Build events normally can not happen within the same frame as they can cause
     * changes to the surrounding environment.  This class encapsulates logic to 
     * execute build events at a throttled rate of once per frame.  All build logic
     * is contained within this class (it used to be in the individual packet processors)
     * because we want the build logic to be re-useable.
     */
    public class ThrottledBuilder : MonoBehaviour
    {
        public static ThrottledBuilder main;

        public event EventHandler QueueDrained;
        private BuildThrottlingQueue buildEvents;
        private IPacketSender packetSender;

        public void Start()
        {
            main = this;
            buildEvents = NitroxServiceLocator.LocateService<BuildThrottlingQueue>();
            packetSender = NitroxServiceLocator.LocateService<IPacketSender>();
        }

        public void Update()
        {
            if(LargeWorldStreamer.main == null || !LargeWorldStreamer.main.IsReady() || !LargeWorldStreamer.main.IsWorldSettled())
            {
                return;
            }

            bool queueHadItems = (buildEvents.Count > 0);

            ProcessBuildEventsUntilFrameBlocked();

            if(queueHadItems && buildEvents.Count == 0 && QueueDrained != null)
            {
                QueueDrained(this, new EventArgs());
            }
        }

        private void ProcessBuildEventsUntilFrameBlocked()
        {
            bool processedFrameBlockingEvent = false;
            bool isNextEventFrameBlocked = false;

            while (buildEvents.Count > 0 && !isNextEventFrameBlocked)
            {
                BuildEvent nextEvent = buildEvents.Dequeue();

                try
                {
                    ActionBuildEvent(nextEvent);
                }
                catch (Exception ex)
                {
                    Log.Error("Error processing buildEvent in ThrottledBuilder" + ex);
                }

                if (nextEvent.RequiresFreshFrame())
                {
                    processedFrameBlockingEvent = true;
                }

                isNextEventFrameBlocked = (processedFrameBlockingEvent && buildEvents.NextEventRequiresFreshFrame());
            }
        }

        private void ActionBuildEvent(BuildEvent buildEvent)
        {
            if (buildEvent is BasePiecePlacedEvent)
            {
                BuildBasePiece((BasePiecePlacedEvent)buildEvent);
            }
            else if (buildEvent is ConstructionCompletedEvent)
            {
                ConstructionCompleted((ConstructionCompletedEvent)buildEvent);
            }
            else if (buildEvent is ConstructionAmountChangedEvent)
            {
                ConstructionAmountChanged((ConstructionAmountChangedEvent)buildEvent);
            }
            else if (buildEvent is DeconstructionBeginEvent)
            {
                DeconstructionBegin((DeconstructionBeginEvent)buildEvent);
            }
            else if (buildEvent is DeconstructionCompletedEvent)
            {
                DeconstructionCompleted((DeconstructionCompletedEvent)buildEvent);
            }
        }

        private void BuildBasePiece(BasePiecePlacedEvent basePiecePlacedBuildEvent)
        {
            Log.Info("BuildBasePiece " + basePiecePlacedBuildEvent.BasePiece.Id + " " + basePiecePlacedBuildEvent.BasePiece.TechType);
            BasePiece basePiece = basePiecePlacedBuildEvent.BasePiece;
            GameObject buildPrefab = CraftData.GetBuildPrefab(basePiece.TechType.Enum());
            MultiplayerBuilder.overridePosition = basePiece.ItemPosition;
            MultiplayerBuilder.overrideQuaternion = basePiece.Rotation;
            MultiplayerBuilder.overrideTransform = new GameObject().transform;
            MultiplayerBuilder.overrideTransform.position = basePiece.CameraPosition;
            MultiplayerBuilder.overrideTransform.rotation = basePiece.CameraRotation;
            MultiplayerBuilder.placePosition = basePiece.ItemPosition;
            MultiplayerBuilder.placeRotation = basePiece.Rotation;
            MultiplayerBuilder.rotationMetadata = basePiece.RotationMetadata;
            MultiplayerBuilder.Begin(buildPrefab);

            GameObject parentBase = null;
            
            if(basePiece.ParentId.IsPresent())
            {
                parentBase = NitroxIdentifier.GetObjectFrom(basePiece.ParentId.Get()).OrElse(null);
            }
            
            Constructable constructable;
            GameObject gameObject;

            if (basePiece.IsFurniture)
            {
                SubRoot subRoot = (parentBase != null) ? parentBase.RequireComponent<SubRoot>() : null;
                                
                gameObject = MultiplayerBuilder.TryPlaceFurniture(subRoot);
                constructable = gameObject.RequireComponentInParent<Constructable>();
            }
            else
            {
                constructable = MultiplayerBuilder.TryPlaceBase(parentBase);
                gameObject = constructable.gameObject;
            }
            
            NitroxIdentifier.SetNewId(gameObject, basePiece.Id);
            
            /**
             * Manually call start to initialize the object as we may need to interact with it within the same frame.
             */
            MethodInfo startCrafting = typeof(Constructable).GetMethod("Start", BindingFlags.NonPublic | BindingFlags.Instance);
            Validate.NotNull(startCrafting);
            startCrafting.Invoke(constructable, new object[] { });
        }

        private void ConstructionCompleted(ConstructionCompletedEvent constructionCompleted)
        {
            Log.Info("Constructed completed " + constructionCompleted.PieceId);
            GameObject constructing = NitroxIdentifier.RequireObjectFrom(constructionCompleted.PieceId);

            ConstructableBase constructableBase = constructing.GetComponent<ConstructableBase>();

            // For bases, we need to transfer the GUID off of the ghost and onto the finished piece.
            // Furniture just re-uses the same piece.
            if(constructableBase)
            {
                constructableBase.constructedAmount = 1f;
                constructableBase.SetState(true, true);
                
                Optional<object> opBasePiece = TransientLocalObjectManager.Get(TransientObjectType.LATEST_CONSTRUCTED_BASE_PIECE);
                GameObject finishedPiece = (GameObject)opBasePiece.Get();
                UnityEngine.Object.Destroy(constructableBase.gameObject);
                NitroxIdentifier.SetNewId(finishedPiece, constructionCompleted.PieceId);
            }
            else
            {
                Constructable constructable = constructing.GetComponent<Constructable>();
                constructable.constructedAmount = 1f;
                constructable.SetState(true, true);
            }
            
            if (constructionCompleted.BaseId != null && NitroxIdentifier.GetObjectFrom(constructionCompleted.BaseId).IsEmpty())
            {
                Log.Info("Creating base: " + constructionCompleted.BaseId);
                ConfigureNewlyConstructedBase(constructionCompleted.BaseId);
            }
        }

        private void ConfigureNewlyConstructedBase(NitroxId newBaseId)
        {
            Optional<object> opNewlyCreatedBase = TransientLocalObjectManager.Get(TransientLocalObjectManager.TransientObjectType.BASE_GHOST_NEWLY_CONSTRUCTED_BASE_GAMEOBJECT);

            if (opNewlyCreatedBase.IsPresent())
            {
                GameObject newlyCreatedBase = (GameObject)opNewlyCreatedBase.Get();
                NitroxIdentifier.SetNewId(newlyCreatedBase, newBaseId);
            }
            else
            {
                Log.Error("Could not assign new base id as no newly constructed base was found");
            }
        }

        private void ConstructionAmountChanged(ConstructionAmountChangedEvent amountChanged)
        {
            Log.Info("Processing ConstructionAmountChanged " + amountChanged.Id + " " + amountChanged.Amount);

            GameObject constructing = NitroxIdentifier.RequireObjectFrom(amountChanged.Id);
            BaseDeconstructable baseDeconstructable = constructing.GetComponent<BaseDeconstructable>();

            // Bases don't  send a deconstruct being packet.  Instead, we just make sure
            // that if we are changing the amount that we set it into deconstruction mode
            // if it still has a BaseDeconstructable object on it.
            if (baseDeconstructable != null)
            {
                baseDeconstructable.Deconstruct();

                // After we have begun the deconstructing for a base piece, we need to transfer the id
                Optional<object> opGhost = TransientLocalObjectManager.Get(TransientObjectType.LATEST_DECONSTRUCTED_BASE_PIECE);

                if(opGhost.IsPresent())
                {
                    GameObject ghost = (GameObject)opGhost.Get();
                    UnityEngine.Object.Destroy(constructing);
                    NitroxIdentifier.SetNewId(ghost, amountChanged.Id);
                }
                else
                {
                    Log.Info("Could not find newly created ghost to set deconstructed id ");
                }
            }
            else
            {
                Constructable constructable = constructing.GetComponentInChildren<Constructable>();
                constructable.constructedAmount = amountChanged.Amount;

                using (packetSender.Suppress<ConstructionAmountChanged>())
                {
                    constructable.Construct();
                }
            }
        }

        private void DeconstructionBegin(DeconstructionBeginEvent begin)
        {
            GameObject deconstructing = NitroxIdentifier.RequireObjectFrom(begin.PieceId);
            Constructable constructable = deconstructing.RequireComponent<Constructable>();

            constructable.SetState(false, false);
        }

        private void DeconstructionCompleted(DeconstructionCompletedEvent completed)
        {
            GameObject deconstructing = NitroxIdentifier.RequireObjectFrom(completed.PieceId);
            UnityEngine.Object.Destroy(deconstructing);
        }
    }
}
