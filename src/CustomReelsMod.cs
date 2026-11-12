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
        private const float SpawnBackoffMeters = 1.5f;
        private const float SpawnLeftOffsetMeters = 1.5f;
        private INewHorizons _nh;
        private Harmony _harmony;
        private readonly TimberHearthMusicGuard _musicGuard = new TimberHearthMusicGuard();
        private SpawnPoint _spawn;
        private Transform _ring;
        private StreamingGroup[] _groups = Array.Empty<StreamingGroup>();
        private OWTriggerVolume[] _candidateVolumes = Array.Empty<OWTriggerVolume>();
        private OWTriggerVolume[] _sectorVolumes = Array.Empty<OWTriggerVolume>();
        private OWTriggerVolume _interiorAtmosphere;
        private VillageMusicVolume[] _villageMusic = Array.Empty<VillageMusicVolume>();
        private readonly HashSet<GameObject> _enteredAtmosphereDetectors = new HashSet<GameObject>();
        private readonly HashSet<OWTriggerVolume> _enteredVolumes = new HashSet<OWTriggerVolume>();
        private readonly HashSet<Shape> _unsupportedPointChecks = new HashSet<Shape>();
        private bool _preparing;
        private bool _floorFound;
        private bool _warped;
        private bool _testingLanding;
        private float _groundedSince;
        private float _nextFloorCheck;
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
                // Move diagonally in the viewing anchor's horizontal plane.
                point.position -= point.right * SpawnLeftOffsetMeters
                    + forward.normalized * SpawnBackoffMeters;
                _spawn._attachedBody = _ring.GetComponent<OWRigidbody>();
                if (_spawn._attachedBody == null)
                {
                    Fallback("Ring World rigidbody not found.");
                    return;
                }
                _groups = _ring.GetComponentsInChildren<StreamingGroup>(true);
                var timberHearth = GameObject.Find("TimberHearth_Body");
                _villageMusic = timberHearth != null
                    ? timberHearth.GetComponentsInChildren<VillageMusicVolume>(true)
                    : Array.Empty<VillageMusicVolume>();
                if (_villageMusic.Length == 0)
                    Log("Timber Hearth VillageMusicVolume not found; music cleanup unavailable.", MessageType.Warning);
                _candidateVolumes = _ring.GetComponentsInChildren<OWTriggerVolume>();
                // This native volume uses airlock entryways, so a Shape.PointInside
                // query alone cannot register a player teleported into the interior.
                _interiorAtmosphere = _ring.GetComponentsInChildren<OWTriggerVolume>(true)
                    .FirstOrDefault(v => v.name == "FluidOxygenVolume");
                if (_interiorAtmosphere == null)
                    Log("Ring interior FluidOxygenVolume not found; oxygen registration unavailable.", MessageType.Warning);
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
                _musicGuard.Begin(_ring, message => Log(message));
                _preparing = true;
                _frames = 0;
                _floorFound = false;
                _warped = false;
                Log($"Preparing cinema: ringLocal={_ring.InverseTransformPoint(point.position):F2}, backoff={SpawnBackoffMeters:F1}m, leftOffset={SpawnLeftOffsetMeters:F1}m, streamingGroups={_groups.Length}");
            }
            catch (Exception error)
            {
                Fallback("Preparation failed: " + error);
            }
        }

        private void LateUpdate()
        {
            _musicGuard.Tick();
            if (!_preparing)
            {
                if (_reportAt > 0 && Time.realtimeSinceStartup >= _reportAt)
                {
                    _reportAt = 0;
                    var player = Locator.GetPlayerBody();
                    if (player != null && _spawn != null)
                    {
                        Log($"After spawn: distanceFromTarget={Vector3.Distance(player.GetPosition(), _spawn.transform.position):F2}m, grounded={player.GetComponent<PlayerCharacterController>()?.IsGrounded()}, relativeSpeed={RelativeSpeed(player):F2}m/s");
                        LogAtmosphereState(player);
                    }
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
                if (Time.realtimeSinceStartup - _started > 30f)
                {
                    Fallback($"Timed out: floorFound={_floorFound}, landing={_testingLanding}, volumes={_spawn._triggerVolumes.Length}.");
                    return;
                }
                if (_testingLanding)
                {
                    // Let physics run without teleporting or resetting velocity.
                    // A ray hit alone does not prove that the player can stand here.
                    var controller = player.GetComponent<PlayerCharacterController>();
                    var distance = Vector3.Distance(player.GetPosition(), _spawn.transform.position);
                    if (distance > 6f)
                    {
                        Fallback($"Landing failed: distance={distance:F2}m, relativeSpeed={RelativeSpeed(player):F2}m/s.");
                        return;
                    }
                    RegisterAtmosphereDetectors(player);
                    LeaveVillageMusic(player);
                    var ground = controller?._groundCollider;
                    var stable = controller != null && controller.IsGrounded()
                        && ground != null && ground.transform.IsChildOf(_ring)
                        && RelativeSpeed(player) < 1f;
                    if (!stable) _groundedSince = 0;
                    else if (_groundedSince == 0) _groundedSince = Time.realtimeSinceStartup;
                    else if (Time.realtimeSinceStartup - _groundedSince >= 1f)
                    {
                        Log($"Cinema spawn ready: grounded=true, ground={ground.name}, volumes={_spawn._triggerVolumes.Length}, elapsed={Time.realtimeSinceStartup - _started:F2}s");
                        LogAtmosphereState(player);
                        _preparing = false;
                        // Keep our streaming request until scene cleanup/fallback.
                        // Do not unload the collision we just used to verify landing.
                        _reportAt = Time.realtimeSinceStartup + 2f;
                    }
                    return;
                }
                PlacePlayer(player);
                LeaveVillageMusic(player);
                RegisterVolumes(detector);
                _frames++;
                if (_frames >= SettleFrames && Time.realtimeSinceStartup >= _nextFloorCheck
                    && _groups.All(g => g == null || g.AreRequiredAssetsLoaded()))
                {
                    _nextFloorCheck = Time.realtimeSinceStartup + 0.5f;
                    if (TryFindFloor(player, out var floor, out var position))
                    {
                        _spawn.transform.position = position;
                        _floorFound = true;
                        PlacePlayer(player);
                        RegisterVolumes(detector);
                        _testingLanding = true;
                        Log($"Floor found: {floor.collider.name}, ringLocal={_ring.InverseTransformPoint(floor.point):F2}, playerLocal={_ring.InverseTransformPoint(position):F2}; checking physical landing.");
                    }
                }
            }
            catch (Exception error)
            {
                Fallback("Spawn guard failed: " + error);
            }
        }

        private bool TryFindFloor(OWRigidbody player, out RaycastHit floor, out Vector3 position)
        {
            var point = _spawn.transform;
            var capsule = player.GetComponent<PlayerCharacterController>()?._bodyCollider as CapsuleCollider;
            if (capsule == null)
                throw new InvalidOperationException("Player body capsule not found; cannot validate spawn clearance.");
            // Prefer the requested back/left offset, then try nearby positions.
            var offsets = from x in Enumerable.Range(-2, 5)
                          from z in Enumerable.Range(-2, 5)
                          orderby x * x + z * z
                          select point.right * (x * 0.75f) + point.forward * (z * 0.75f);
            foreach (var offset in offsets)
            {
                foreach (var hit in Physics.RaycastAll(point.position + offset + point.up * 0.5f,
                             -point.up, 6f, ~0, QueryTriggerInteraction.Ignore).OrderBy(h => h.distance))
                {
                    if (!hit.collider.transform.IsChildOf(_ring) || !CanCollide(capsule, hit.collider)
                        || Vector3.Dot(hit.normal, point.up) < 0.9f) continue;
                    GetCapsuleAt(player, capsule, point.position, out var a, out var b, out var radius);
                    var bottom = Mathf.Min(Vector3.Dot(a - point.position, point.up),
                        Vector3.Dot(b - point.position, point.up)) - radius;
                    var candidate = hit.point + point.up * (0.1f - bottom);
                    GetCapsuleAt(player, capsule, candidate, out a, out b, out radius);
                    if (Physics.OverlapCapsule(a, b, radius + 0.03f, ~0, QueryTriggerInteraction.Ignore)
                        .Any(c => !c.transform.IsChildOf(player.transform) && CanCollide(capsule, c))) break;
                    floor = hit;
                    position = candidate;
                    return true;
                }
            }
            floor = default;
            position = default;
            return false;
        }

        private static bool CanCollide(Collider playerCollider, Collider other)
            => !Physics.GetIgnoreLayerCollision(playerCollider.gameObject.layer, other.gameObject.layer)
                && !Physics.GetIgnoreCollision(playerCollider, other);

        private void GetCapsuleAt(OWRigidbody player, CapsuleCollider capsule, Vector3 position,
            out Vector3 a, out Vector3 b, out float radius)
        {
            var scale = capsule.transform.lossyScale;
            var axis = capsule.direction;
            radius = capsule.radius * Mathf.Max(Mathf.Abs(scale[(axis + 1) % 3]), Mathf.Abs(scale[(axis + 2) % 3]));
            var halfSegment = Mathf.Max(0, capsule.height * Mathf.Abs(scale[axis]) * 0.5f - radius);
            var rotation = _spawn.transform.rotation * Quaternion.Inverse(player.transform.rotation);
            var center = position + rotation * (capsule.transform.TransformPoint(capsule.center) - player.transform.position);
            var localAxis = axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward;
            var segment = rotation * capsule.transform.TransformDirection(localAxis) * halfSegment;
            a = center + segment;
            b = center - segment;
        }

        private float RelativeSpeed(OWRigidbody player)
            => (player.GetVelocity() - _spawn._attachedBody.GetVelocity()
                - _spawn._attachedBody.GetPointTangentialVelocity(player.GetPosition())).magnitude;

        private void LeaveVillageMusic(OWRigidbody player)
        {
            // Startup can register the camera in the village even when NH moves
            // the body elsewhere. Clear both trigger membership and audio priority
            // tracking while settling, including a dormant daytime music layer.
            var audioDetectors = player.GetComponentsInChildren<AudioDetector>(true);
            var objects = audioDetectors.Select(d => d.gameObject)
                .Concat(new[] { Locator.GetPlayerDetector() }).Where(o => o != null).Distinct().ToArray();
            foreach (var volume in _villageMusic)
            {
                if (volume == null) continue;
                var changed = false;
                var triggers = new[] { volume._triggerVolumeOverride, volume.GetOWTriggerVolume() }
                    .Where(v => v != null).Distinct();
                foreach (var trigger in triggers)
                    foreach (var obj in objects)
                        if (trigger.IsTrackingObject(obj))
                        {
                            trigger.RemoveObjectFromVolume(obj);
                            changed = true;
                        }
                foreach (var detector in audioDetectors)
                {
                    // The trigger may already have lost the object during warp,
                    // while the detector still retains an inactive priority layer.
                    if ((detector._activeVolumes != null && detector._activeVolumes.Contains(volume))
                        || (detector._trackedLayers != null
                            && detector._trackedLayers.Values.Any(layer => layer.volumes.Contains(volume))))
                    {
                        detector.RemoveVolume(volume);
                        changed = true;
                    }
                }
                if (changed || volume.IsActive() || (volume._owAudioSrc != null && volume._owAudioSrc.isPlaying))
                {
                    volume.Deactivate();
                    volume._owAudioSrc?.Stop();
                    Log($"Village music detached: {volume.name}.");
                }
            }
        }

        private void RegisterVolumes(GameObject detector)
        {
            // NH initializes custom SpawnPoints with no trigger volumes. Entering
            // the containing volumes enables sectors, collision and atmosphere.
            var volumes = _candidateVolumes
                .Where(ContainsSpawnPoint)
                .Concat(_sectorVolumes.Where(v => v != null))
                .Concat(new[] { _interiorAtmosphere }.Where(v => v != null && v.isActiveAndEnabled))
                .Distinct().ToArray();
            _spawn._triggerVolumes = volumes;
            foreach (var previous in _enteredVolumes.Where(v => v != null && !volumes.Contains(v)).ToArray())
            {
                if (previous.IsTrackingObject(detector)) previous.RemoveObjectFromVolume(detector);
                _enteredVolumes.Remove(previous);
            }
            foreach (var volume in volumes)
            {
                if (volume.IsTrackingObject(detector)) continue;
                volume.AddObjectToVolume(detector);
                _enteredVolumes.Add(volume);
            }
            RegisterAtmosphereDetectors(Locator.GetPlayerBody());
        }

        private void RegisterAtmosphereDetectors(OWRigidbody player)
        {
            if (_interiorAtmosphere == null || !_interiorAtmosphere.isActiveAndEnabled || player == null) return;
            var resources = player.GetComponent<PlayerResources>();
            if (resources == null) return;
            // Register the actual oxygen/body/camera detectors through the native
            // trigger. Its normal airlock exit events will remove them again.
            var detectors = new Detector[] { resources._oxygenDetector, resources._fluidDetector, resources._cameraFluidDetector };
            foreach (var obj in detectors.Where(d => d != null).Select(d => d.gameObject).Distinct())
            {
                if (_interiorAtmosphere.IsTrackingObject(obj)) continue;
                _interiorAtmosphere.AddObjectToVolume(obj);
                _enteredAtmosphereDetectors.Add(obj);
            }
        }

        private void LogAtmosphereState(OWRigidbody player)
        {
            var resources = player.GetComponent<PlayerResources>();
            if (resources == null) return;
            Log($"Atmosphere: volume={_interiorAtmosphere?.name ?? "missing"}, oxygenPresent={resources.IsOxygenPresent()}, oxygen={resources.GetOxygenFraction():P0}, refilling={resources.IsRefillingOxygen()}");
        }

        private void RemoveAtmosphereDetectors()
        {
            if (_interiorAtmosphere != null)
                foreach (var obj in _enteredAtmosphereDetectors)
                    if (obj != null && _interiorAtmosphere.IsTrackingObject(obj))
                        _interiorAtmosphere.RemoveObjectFromVolume(obj);
            _enteredAtmosphereDetectors.Clear();
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
            _musicGuard.Reset();
            _preparing = false;
            Log(reason + " Returning to the vanilla Timber Hearth spawn.", MessageType.Error);
            var detector = Locator.GetPlayerDetector();
            foreach (var volume in _enteredVolumes)
                if (volume != null && detector != null && volume.IsTrackingObject(detector))
                    volume.RemoveObjectFromVolume(detector);
            _enteredVolumes.Clear();
            RemoveAtmosphereDetectors();
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
            _musicGuard.Reset();
            _preparing = false;
            _reportAt = 0;
            _testingLanding = false;
            _groundedSince = 0;
            _nextFloorCheck = 0;
            RemoveAtmosphereDetectors();
            _interiorAtmosphere = null;
            _villageMusic = Array.Empty<VillageMusicVolume>();
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
