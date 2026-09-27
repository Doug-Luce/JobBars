using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using JobBars.Data;
using JobBars.Helper;
using JobBars.Nodes.Cooldown;
using KamiToolKit.Controllers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace JobBars.Cooldowns.Manager {
    public struct CooldownPartyMemberStruct {
        public uint ObjectId;
        public JobIds Job;
    }

    public unsafe partial class CooldownManager : PerJobManager<CooldownConfig[]> {
        private static readonly int MILLIS_LOOP = 250;
        private Dictionary<ulong, CooldownPartyMember> ObjectIdToMember = [];
        private readonly Dictionary<JobIds, List<CooldownConfig>> CustomCooldowns = [];

        private AddonController<AddonPartyList> Controller;
        private CooldownRoot? Root;

        public CooldownManager() : base( "##JobBars_Cooldowns" ) {
            // Initialize custom cooldowns, remove duplicates
            foreach( var custom in JobBars.Configuration.CustomCooldown.GroupBy( x => x.GetNameId() ).Select( x => x.First() ).ToList() ) {
                if( !CustomCooldowns.ContainsKey( custom.Job ) ) CustomCooldowns[custom.Job] = [];
                CustomCooldowns[custom.Job].Add( new CooldownConfig( custom.Name, custom.GetNameId(), custom.Props ) );
            }

            Controller = new AddonController<AddonPartyList> {
                AddonName = "_PartyList",
                OnSetup = SetupAddon,
                OnFinalize = ResetAddon,
                OnUpdate = UpdateAddon
            };
            Controller.Enable();
        }

        private void SetupAddon( AddonPartyList* addon ) {
            Root = new( this );
            Root.AttachNode( (AtkUnitBase*)addon );
        }

        private void ResetAddon( AddonPartyList* addon ) {
            ClearTooltip();
            Root?.Dispose();
            Root = null;
        }

        private void UpdateAddon( AddonPartyList* addon ) {
            Tick();
        }

        public void Hide() {
            ClearTooltip();
            Root?.IsVisible = false;
        }

        public void Dispose() {
            Controller?.Dispose();
            Controller = null;
            Root = null;
        }

        public CooldownConfig[] GetCooldownConfigs( JobIds job ) {
            List<CooldownConfig> configs = [];
            if( JobToValue.TryGetValue( job, out var props ) ) configs.AddRange( props );
            if( CustomCooldowns.TryGetValue( job, out var customProps ) ) configs.AddRange( customProps );
            return [.. configs];
        }

        public void PerformAction( Item action, uint objectId ) {
            if( !JobBars.Configuration.CooldownsEnabled ) return;

            foreach( var member in ObjectIdToMember.Values ) {
                member.ProcessAction( action, objectId );
            }
        }

        // Drawn by us in ImGui, because the overlay addon these nodes live on is click-through
        // and never dispatches native mouse events to them.
        private CooldownNode? TooltipNode;

        // Hit-tested by us and shown via the node's own ShowTooltip, which gives the real
        // in-game action tooltip without needing the addon to dispatch a MouseOver.
        public void DrawTooltip() {
            var hovered = FindHovered();
            if( hovered == TooltipNode ) return;

            TooltipNode?.HideTooltip();
            TooltipNode = hovered;
            TooltipNode?.ShowTooltip();
        }

        private CooldownNode? FindHovered() {
            if( Root == null || !Root.IsVisible ) return null;

            var scale = JobBars.Configuration.CooldownScale * UiHelper.GetAddonScale( "_PartyList" );
            var width = CooldownNode.WIDTH * scale;
            var height = CooldownNode.HEIGHT * scale;

            foreach( var row in Root.Rows ) {
                if( !row.IsVisible ) continue;
                foreach( var node in row.Nodes ) {
                    if( !node.IsVisible || node.IconId == 0 ) continue;
                    if( UiHelper.MouseOver( node.ScreenPosition, width, height ) ) return node;
                }
            }

            return null;
        }

        private void ClearTooltip() {
            TooltipNode?.HideTooltip();
            TooltipNode = null;
        }

        public void Tick() {
            if( Root == null ) return;

            // Visibility

            if( UiHelper.CalcDoHide( JobBars.Configuration.CooldownsEnabled, JobBars.Configuration.CooldownsHideOutOfCombat, JobBars.Configuration.CooldownsHideWeaponSheathed ) ) {

                Root!.IsVisible = false;
                return;
            }
            else {
                Root!.IsVisible = true;
            }

            // Global position + scale

            Root.Position = JobBars.Configuration.CooldownPosition + new Vector2( 0, UiHelper.PartyListOffset() );
            Root.Scale = new( JobBars.Configuration.CooldownScale, JobBars.Configuration.CooldownScale );
            Root.UpdateSpacing();

            // Trick

            var time = DateTime.Now;
            var millis = time.Second * 1000 + time.Millisecond;
            var percent = ( float )( millis % MILLIS_LOOP ) / MILLIS_LOOP;

            Dictionary<ulong, CooldownPartyMember> newObjectIdToMember = [];

            if( JobBars.PartyMembers == null ) Dalamud.Error( "PartyMembers is null" );

            for( var idx = 0; idx < JobBars.PartyMembers!.Count; idx++ ) {
                var partyMember = JobBars.PartyMembers[idx];

                if( partyMember == null || partyMember?.ObjectId == 0 || partyMember?.Job == JobIds.OTHER ) {
                    Root!.SetCooldownRowVisible( idx, false );
                    continue;
                }

                if( !JobBars.Configuration.CooldownsShowPartyMembers && partyMember.ObjectId != Dalamud.Objects.LocalPlayer.GameObjectId ) {
                    Root!.SetCooldownRowVisible( idx, false );
                    continue;
                }

                var member = ObjectIdToMember.TryGetValue( partyMember.ObjectId, out var _member ) ? _member : new CooldownPartyMember( partyMember.ObjectId );
                member.Tick( Root!.Rows[idx], partyMember, percent );
                newObjectIdToMember[partyMember.ObjectId] = member;

                Root!.SetCooldownRowVisible( idx, true );
            }

            for( var idx = JobBars.PartyMembers.Count; idx < 8; idx++ ) { // hide remaining slots
                Root!.SetCooldownRowVisible( idx, false );
            }

            ObjectIdToMember = newObjectIdToMember;
        }

        public void ResetUi() => ObjectIdToMember.Clear();

        public void Reset() {
            foreach( var item in ObjectIdToMember.Values ) item.Reset();
        }

        public void AddCustomCooldown( JobIds job, string name, CooldownProps props ) {
            if( !CustomCooldowns.ContainsKey( job ) ) CustomCooldowns[job] = [];
            var newCustom = JobBars.Configuration.AddCustomCooldown( name, job, props );
            CustomCooldowns[job].Add( new CooldownConfig( name, newCustom.GetNameId(), props ) );
        }

        public void DeleteCustomCooldown( JobIds job, CooldownConfig custom ) {
            CustomCooldowns[job].Remove( custom );
            JobBars.Configuration.RemoveCustomCooldown( custom.NameId );
        }
    }
}