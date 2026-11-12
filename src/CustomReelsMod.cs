using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using OWML.Common;
using OWML.ModHelper;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace CustomReels
{
    public interface INewHorizons
    {
        void LoadConfigs(IModBehaviour mod);
        UnityEvent<string> GetStarSystemLoadedEvent();
        Transform GetPlayerSpawnPoint();
    }

    public sealed class CustomReelsMod : ModBehaviour
    {
        private const string SpawnName = "CustomReels_CinemaSpawn";
        private const string ProjectorPath = "Sector_RingInterior/Sector_Zone1/Sector_ProjectionHouse_Zone1/Interactables_ProjectionHouse_Zone1/TheatreRoom_ProjectionHouse_Zone1/Prefab_IP_SlideProjector_Ring";
        // NH can schedule two successive 30-update spawn/velocity corrections.
        private const int SettleFrames = 90;
        private INewHorizons _nh;
        private Harmony _harmony;
        private SpawnPoint _spawn;
        private Transform _ring;
        private StreamingGroup[] _groups = Array.Empty<StreamingGroup>();
        private OWTriggerVolume[] _candidateVolumes = Array.Empty<OWTriggerVolume>();
        private OWTriggerVolume[] _sectorVolumes = Array.Empty<OWTriggerVolume>();
        private readonly HashSet<OWTriggerVolume> _enteredVolumes = new HashSet<OWTriggerVolume>();
        private readonly HashSet<Shape> _unsupportedPointChecks = new HashSet<Shape>();
        private bool _preparing;
        private bool _floorFound;
        private bool _warped;
        private int _frames;
        private float _started;
        private float _reportAt;

        private void Start()
        {
            _harmony = new Harmony("usopshiy.CustomReels");
            _harmony.PatchAll(typeof(SlideStreamingPatch).Assembly);
            _nh = ModHelper.Interaction.TryGetModApi<INewHorizons>("xen.NewHorizons");
            if (_nh == null)
            {
                Log("New Horizons API is unavailable.", MessageType.Error);
                return;
            }
            _nh.GetStarSystemLoadedEvent().AddListener(OnSystemLoaded);
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            _nh.LoadConfigs(this);
        }

        private void OnSystemLoaded(string system)
        {
            Cleanup();
            if (system != "SolarSystem") return;
            var point = _nh.GetPlayerSpawnPoint();
            // Respect isDefault:false and spawn overrides from other mods.
            if (point == null || point.name != SpawnName)
            {
                Log("Cinema spawn is not selected; leaving the active spawn unchanged.");
                return;
            }
            _spawn = point.GetComponent<SpawnPoint>();
            if (_spawn == null) return;
            try
            {
                _ring = GameObject.Find("RingWorld_Body")?.transform;
                var projector = _ring?.Find(ProjectorPath)?.GetComponent<SlideProjector>();
                if (projector == null || projector._lockOnTransform == null)
                {
                    Fallback("Vanilla projector or viewing anchor not found.");
                    return;
                }
                Log($"Configured spawn: ringLocal={_ring.InverseTransformPoint(point.position):F2}, parent={point.parent?.name}");
                // Use the game's viewing anchor, not an assumed projector-local offset.
                var anchor = projector._lockOnTransform;
                if (!anchor.IsChildOf(_ring))
                {
                    Fallback("Projector viewing anchor is outside Ring World hierarchy.");
                    return;
                }
                point.SetParent(anchor, false);
                point.localPosition = Vector3.zero;
                var forward = Vector3.ProjectOnPlane(anchor.forward, projector.transform.up);
                if (forward.sqrMagnitude < 0.001f) forward = projector.transform.forward;
                point.rotation = Quaternion.LookRotation(forward, projector.transform.up);
                _spawn._attachedBody = _ring.GetComponent<OWRigidbody>();
                if (_spawn._attachedBody == null)
                {
                    Fallback("Ring World rigidbody not found.");
                    return;
                }
                _groups = _ring.GetComponentsInChildren<StreamingGroup>(true);
                _candidateVolumes = _ring.GetComponentsInChildren<OWTriggerVolume>();
                _sectorVolumes = projector.GetComponentsInParent<Sector>()
                    .Select(s => s.GetTriggerVolume()).Where(v => v != null).ToArray();
                foreach (var group in _groups)
                {
                    group.RequestRequiredAssets(100);
                    group.RequestGeneralAssets(100);
                    // Some groups defer colliders until the wake-up animation finishes.
                    group.LoadRequiredColliders(100);
                }
                _started = Time.realtimeSinceStartup;
                _preparing = true;
                _frames = 0;
                _floorFound = false;
                _warped = false;
                Log($"Preparing cinema: ringLocal={_ring.InverseTransformPoint(point.position):F2}, streamingGroups={_groups.Length}");
            }
            catch (Exception error)
            {
                Fallback("Preparation failed: " + error);
            }
        }

        private void LateUpdate()
        {
            if (!_preparing)
            {
                if (_reportAt > 0 && Time.realtimeSinceStartup >= _reportAt)
                {
                    _reportAt = 0;
                    var player = Locator.GetPlayerBody();
                    if (player != null && _spawn != null)
                        Log($"After spawn: distanceFromTarget={Vector3.Distance(player.GetPosition(), _spawn.transform.position):F2}m");
                }
                return;
            }
            if (_spawn == null || _ring == null)
            {
                Cleanup();
                return;
            }
            try
            {
                var player = Locator.GetPlayerBody();
                var detector = Locator.GetPlayerDetector();
                if (player == null || detector == null)
                {
                    if (Time.realtimeSinceStartup - _started > 30f)
                        Fallback("Player body/detector did not become available.");
                    return;
                }
                PlacePlayer(player);
                RegisterVolumes(detector);
                if (!_floorFound && TryFindFloor(out var floor))
                {
                    // SpawnPoint's player capsule extends one metre below its centre.
                    _spawn.transform.position = floor.point + _spawn.transform.up * 1.1f;
                    _floorFound = true;
                    PlacePlayer(player);
                    RegisterVolumes(detector);
                    Log($"Floor found: {floor.collider.name}, ringLocal={_ring.InverseTransformPoint(floor.point):F2}");
                }
                _frames++;
                if (_floorFound && _frames >= SettleFrames && _groups.All(g => g == null || g.AreRequiredAssetsLoaded()))
                {
                    Log($"Cinema spawn ready: volumes={_spawn._triggerVolumes.Length}, elapsed={Time.realtimeSinceStartup - _started:F2}s");
                    _preparing = false;
                    ReleaseStreaming();
                    _reportAt = Time.realtimeSinceStartup + 2f;
                }
                else if (Time.realtimeSinceStartup - _started > 30f)
                {
                    Fallback($"Timed out: floorFound={_floorFound}, volumes={_spawn._triggerVolumes.Length}.");
                }
            }
            catch (Exception error)
            {
                Fallback("Spawn guard failed: " + error);
            }
        }

        private bool TryFindFloor(out RaycastHit floor)
        {
            var point = _spawn.transform;
            foreach (var hit in Physics.RaycastAll(point.position + point.up * 0.5f,
                         -point.up, 6f, ~0, QueryTriggerInteraction.Ignore).OrderBy(h => h.distance))
            {
                if (hit.collider.transform.IsChildOf(_ring) && Vector3.Dot(hit.normal, point.up) > 0.6f)
                {
                    floor = hit;
                    return true;
                }
            }
            floor = default;
            return false;
        }

        private void RegisterVolumes(GameObject detector)
        {
            // NH initializes custom SpawnPoints with no trigger volumes. Entering
            // the containing volumes enables sectors, collision and atmosphere.
            var volumes = _candidateVolumes
                .Where(ContainsSpawnPoint)
                .Concat(_sectorVolumes.Where(v => v != null)).Distinct().ToArray();
            _spawn._triggerVolumes = volumes;
            foreach (var volume in volumes)
            {
                if (volume.IsTrackingObject(detector)) continue;
                volume.AddObjectToVolume(detector);
                _enteredVolumes.Add(volume);
            }
        }

        private bool ContainsSpawnPoint(OWTriggerVolume volume)
        {
            if (volume == null || !volume.isActiveAndEnabled) return false;
            var shape = volume.GetShape();
            if (shape == null || _unsupportedPointChecks.Contains(shape)) return false;
            try
            {
                // We need membership, not penetration depth. CylinderShape's
                // PenetrationDistance throws NotImplementedException in game.
                return shape.PointInside(_spawn.transform.position);
            }
            catch (NotImplementedException)
            {
                // An unsupported optional shape must not abort the whole spawn.
                // Sector volumes are added separately; other volumes can still
                // detect entry through the game's normal collision handling.
                _unsupportedPointChecks.Add(shape);
                Log($"Point containment unavailable: {shape.GetType().Name} ({volume.name}); leaving entry to the game.", MessageType.Warning);
                return false;
            }
        }

        private void PlacePlayer(OWRigidbody player)
        {
            var point = _spawn.transform;
            if (!_warped)
            {
                player.WarpToPositionRotation(point.position, point.rotation);
                _warped = true;
            }
            else
            {
                // As in NH's settling routine, avoid recentering the universe every frame.
                player.transform.SetPositionAndRotation(point.position, point.rotation);
            }
            var body = _spawn._attachedBody;
            player.SetVelocity(body.GetVelocity() + body.GetPointTangentialVelocity(point.position));
        }

        private void Fallback(string reason)
        {
            _preparing = false;
            Log(reason + " Returning to the vanilla Timber Hearth spawn.", MessageType.Error);
            var detector = Locator.GetPlayerDetector();
            foreach (var volume in _enteredVolumes)
                if (volume != null && detector != null && volume.IsTrackingObject(detector))
                    volume.RemoveObjectFromVolume(detector);
            _enteredVolumes.Clear();
            ReleaseStreaming();
            var vanilla = FindObjectsOfType<SpawnPoint>().FirstOrDefault(s =>
                s != _spawn && s.GetSpawnLocation() == SpawnLocation.TimberHearth && !s.IsShipSpawn());
            if (_spawn == null || vanilla == null)
            {
                Log("Fallback spawn unavailable.", MessageType.Error);
                return;
            }
            _spawn.transform.SetParent(vanilla.transform.parent, true);
            _spawn.transform.SetPositionAndRotation(vanilla.transform.position, vanilla.transform.rotation);
            _spawn._attachedBody = vanilla._attachedBody;
            _spawn._triggerVolumes = vanilla._triggerVolumes;
            _warped = false;
            var player = Locator.GetPlayerBody();
            if (player != null) PlacePlayer(player);
            if (detector != null) _spawn.AddObjectToTriggerVolumes(detector);
        }

        private void ReleaseStreaming()
        {
            foreach (var group in _groups)
            {
                if (group == null) continue;
                group.ReleaseGeneralAssets();
                group.ReleaseRequiredAssets();
            }
            _groups = Array.Empty<StreamingGroup>();
        }

        private void Cleanup()
        {
            _preparing = false;
            _reportAt = 0;
            ReleaseStreaming();
            _enteredVolumes.Clear();
            _spawn = null;
            _unsupportedPointChecks.Clear();
            _ring = null;
            _candidateVolumes = Array.Empty<OWTriggerVolume>();
            _sectorVolumes = Array.Empty<OWTriggerVolume>();
        }

        private void OnSceneUnloaded(Scene scene) => Cleanup();

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            _nh?.GetStarSystemLoadedEvent().RemoveListener(OnSystemLoaded);
            Cleanup();
        }

        private void Log(string message, MessageType type = MessageType.Info)
            => ModHelper.Console.WriteLine("[CustomReels Spawn] " + message, type);
    }
}
