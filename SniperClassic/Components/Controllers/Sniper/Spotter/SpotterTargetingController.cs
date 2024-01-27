﻿using RoR2.Orbs;
using RoR2;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using SniperClassic.Modules;
using SniperClassic.Controllers;

namespace SniperClassic
{
    /*
        - Notes:
            - trackingTarget is calculated ClientSide. This is why CmdSendSpotter sends a netID
                - The Client is able to remember the netID
     */

    public class SpotterTargetingController : NetworkBehaviour
    {
        [Command]
        private void CmdSetSpotterMode(int mode)
        {
            spotterMode = (SpotterMode)mode;
        }

        [Command]
        private void CmdSendSpotter(uint masterID)
        {
            if (masterID != uint.MaxValue)
            {
                __spotterLockedOn = true;
                spotterFollower.spotterMode = spotterMode;
                spotterFollower.__AssignNewTarget(masterID);
            }
            else
            {
                ServerReturnSpotter();
            }
        }

        [Client]
        public void ClientSendSpotter(SpotterMode mode)
        {
            uint netID = uint.MaxValue;

            if (hasTrackingTarget && trackingTarget.healthComponent && trackingTarget.healthComponent.body && trackingTarget.healthComponent.body.master)
            {
                NetworkIdentity n = trackingTarget.healthComponent.body.master.GetComponent<NetworkIdentity>();
                if (n) netID = n.netId.Value;
            }
            CmdSetSpotterMode((int)mode);
            CmdSendSpotter(netID);
        }

        public void ClientReturnSpotter()
        {
            if (this.hasAuthority) CmdReturnSpotter();
        }

        [Server]
        public void ServerForceEndSpotterSkill()
        {
            if (NetworkServer.active)
            {
                __spotterLockedOn = false;
                spotterFollower.__AssignNewTarget(uint.MaxValue);
                RpcForceEndSpotterSkill();
            }
        }

        [ClientRpc]
        private void RpcForceEndSpotterSkill()
        {
            if (base.hasAuthority)
            {
                ForceEndSpotterSkill();
            }
        }

        [Command]
        private void CmdReturnSpotter()
        {
            ServerReturnSpotter();
        }

        [Server]
        private void ServerReturnSpotter()
        {
            if (!NetworkServer.active) return;
            __spotterLockedOn = false;
            spotterFollower.__AssignNewTarget(uint.MaxValue);
        }

        private void ForceEndSpotterSkill()
        {
            if (!base.hasAuthority || !characterBody.skillLocator || !characterBody.skillLocator.special) return;
            EntityStateMachine stateMachine = characterBody.skillLocator.special.stateMachine;
            if (!stateMachine) return;

            EntityStates.SniperClassicSkills.SendSpotter sendSpotter = stateMachine.state as EntityStates.SniperClassicSkills.SendSpotter;
            if (sendSpotter != null) sendSpotter.OnExit();
        }

        private void Start()
        {
            this.characterBody = base.GetComponent<CharacterBody>();
            this.inputBank = base.GetComponent<InputBankTest>();
            this.teamComponent = base.GetComponent<TeamComponent>();
        }

        private void FixedUpdate()
        {
            if (characterBody.skillLocator.special.stock < 1)
            {
                OnDisable();
            }
            else if (!this.indicator.active)
            {
                OnEnable();
            }

            if (NetworkServer.active)
            {
                if (!this.spotterFollower)
                {
                    SpawnSpotter();
                }
            }
            else
            {
                if (__hasSpotter && !this.spotterFollower)
                {
                    if (this.hasAuthority) CmdUpdateSpotter();
                }
                else if (this.spotterFollower && !this.spotterFollower.setOwner)
                {
                    this.spotterFollower.OwnerBodyObject = base.gameObject;
                    this.spotterFollower.ownerBody = characterBody;
                    this.spotterFollower.rechargeController = this.rechargeController;
                    this.spotterFollower.setOwner = true;
                }
            }
            
            if (!__spotterLockedOn)
            {
                if (!this.indicator.hasVisualizer)
                {
                    this.indicator.InstantiateVisualizer();
                }
                this.trackerUpdateStopwatch += Time.fixedDeltaTime;
                if (this.trackerUpdateStopwatch >= 1f / this.trackerUpdateFrequency)
                {
                    this.trackerUpdateStopwatch -= 1f / this.trackerUpdateFrequency;
                    Ray aimRay = new Ray(this.inputBank.aimOrigin, this.inputBank.aimDirection);
                    this.SearchForTarget(aimRay);
                }
            }
            else
            {
                if (this.indicator.hasVisualizer)
                {
                    this.indicator.DestroyVisualizer();
                }
                if (!this.trackingTarget || !this.trackingTarget.healthComponent.alive)
                {
                    this.trackingTarget = null;
                    this.hasTrackingTarget = false;
                    if (base.hasAuthority)
                    {
                        CmdReturnSpotter();
                        ForceEndSpotterSkill();
                    }
                }
            }

            this.indicator.targetTransform = (this.trackingTarget ? this.trackingTarget.transform : null);
        }

        [Command]
        private void CmdUpdateSpotter()
        {
            if (this.spotterFollower)
            {
                RpcSetSpotterFollower(spotterFollower.GetComponent<NetworkIdentity>().netId.Value);
            }
            else
            {
                this.__hasSpotter = false;
            }
        }

        [ClientRpc]
        void RpcSetSpotterFollower(uint id)
        {
            if (!NetworkServer.active)
            {
                GameObject go = ClientScene.FindLocalObject(new NetworkInstanceId(id));
                if (!go)
                {
                    return;
                }
                this.spotterFollower = go.GetComponent<SpotterFollowerController>();
            }
        }

        [Server]
        private void SpawnSpotter()
        {
            GameObject gameObject = UnityEngine.Object.Instantiate<GameObject>(spotterFollowerGameObject, base.transform.position, Quaternion.identity);
            this.spotterFollower = gameObject.GetComponent<SpotterFollowerController>();
            this.spotterFollower.OwnerBodyObject = base.gameObject;
            this.spotterFollower.ownerBody = characterBody;
            this.spotterFollower.rechargeController = this.rechargeController;

            //Guard against nullrefs if spawning a masterless body
            GameObject masterObject = characterBody ? characterBody.masterObject : null;
            NetworkIdentity n = masterObject ? masterObject.GetComponent<NetworkIdentity>() : null;
            uint newNetId = uint.MaxValue;
            if (n) newNetId = n.netId.Value;
            this.spotterFollower.__ownerMasterNetID = newNetId;

            this.spotterFollower.setOwner = true;
            this.spotterFollower.targetingController = this;
            NetworkServer.Spawn(gameObject);
            __hasSpotter = true;
            __spotterFollowerNetID = spotterFollower.GetComponent<NetworkIdentity>().netId.Value;
            RpcSetSpotterFollower(__spotterFollowerNetID);
        }

        private void SearchForTarget(Ray aimRay)
        {
            this.search.teamMaskFilter = TeamMask.GetUnprotectedTeams(this.teamComponent.teamIndex);
            this.search.filterByLoS = true;
            this.search.searchOrigin = aimRay.origin;
            this.search.searchDirection = aimRay.direction;
            this.search.sortMode = BullseyeSearch.SortMode.Angle;
            this.search.maxDistanceFilter = this.maxTrackingDistance;
            this.search.maxAngleFilter = this.maxTrackingAngle;
            this.search.RefreshCandidates();
            this.search.FilterOutGameObject(base.gameObject);
            this.trackingTarget = this.search.GetResults().FirstOrDefault<HurtBox>();
            if (this.trackingTarget && this.trackingTarget.healthComponent && this.trackingTarget.healthComponent.body
                && (!this.trackingTarget.healthComponent.body.HasBuff(SniperContent.spotterStatDebuff))
                && this.trackingTarget.healthComponent.body.masterObject)
            {
                this.hasTrackingTarget = true;
                return;
            }
            this.hasTrackingTarget = false;
            this.trackingTarget = null;
        }

        private void Awake()
        {
            this.indicator = new Indicator(base.gameObject, targetIndicator);
            this.characterBody = base.gameObject.GetComponent<CharacterBody>();
            this.rechargeController = base.gameObject.GetComponent<SpotterRechargeController>();
        }

        private void OnDestroy()
        {
            if (NetworkServer.active)
            {
                if (spotterFollower)
                {
                    Destroy(spotterFollower.gameObject);
                }
            }
            if (this.indicator != null)
            {
                this.indicator.active = false;
                this.indicator.DestroyVisualizer();
            }
        }

        public HurtBox GetTrackingTarget()
        {
            return this.trackingTarget;
        }

        private void OnEnable()
        {
            this.indicator.active = true;
        }

        private void OnDisable()
        {
            this.indicator.active = false;
        }

        public static GameObject targetIndicator;

        public float maxTrackingDistance = 2000f;
        public float maxTrackingAngle = 90f;
        public float trackerUpdateFrequency = 10f;

        private SpotterMode spotterMode = SpotterMode.ChainLightning;

        private HurtBox trackingTarget;

        [SyncVar]
        private bool __spotterLockedOn = false;

        [SyncVar]
        private uint __spotterFollowerNetID = uint.MaxValue;

        private bool hasTrackingTarget = false;

        private uint trackingTargetNetID;

        private CharacterBody characterBody;
        private TeamComponent teamComponent;
        private InputBankTest inputBank;
        private float trackerUpdateStopwatch;
        private Indicator indicator;
        private readonly BullseyeSearch search = new BullseyeSearch();
        private SpotterRechargeController rechargeController;

        public SpotterFollowerController spotterFollower;

        [SyncVar]
        private bool __hasSpotter = false;

        public static GameObject spotterFollowerGameObject = null;
    }

    public enum SpotterMode
    {
        ChainLightning,
        ChainLightningScepter,
        Disrupt,
        DisruptScepter
    }
}
