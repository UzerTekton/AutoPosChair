// AutoPosChair 2.0.0
// Uzer Tekton
// MIT License


using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common.Enums;
using Random = System.Random;
#if !COMPILER_UDONSHARP && UNITY_EDITOR
using UnityEditor;
using UdonSharpEditor;
#endif

namespace UzerTekton.AutoPosChair
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class AutoPosChair : UdonSharpBehaviour
    {
        // Drawing gizmo in editor
        #if !COMPILER_UDONSHARP && UNITY_EDITOR

        private BoxCollider _triggerCollider; // To get the size
        private bool _canDrawGizmo;

        private static Color _gizmoInteractCubeColor = new Color(0.25f, 0.75f, 1, 0.25f);
        private static Color _gizmoChairEdgeCenterColor = new Color(1, 0, 0, 0.25f);
        private Vector3 _gizmoChairEdgeCenterCubeSize;
        [SerializeField] private bool alwaysShowGizmo = true;

        private void OnValidate()
        {
            if (!TryGetComponent<BoxCollider>(out _triggerCollider) || !chairEdgeTransform)
            {
                _canDrawGizmo = false;
                return;
            }

            _canDrawGizmo = true;

            _gizmoChairEdgeCenterCubeSize = new Vector3(0, _triggerCollider.size.x * 0.125f, _triggerCollider.size.x * 0.125f);
        }

        private void OnDrawGizmos()
        {
            if (!_canDrawGizmo || !alwaysShowGizmo) return;
            DrawGizmo();
        }


        private void OnDrawGizmosSelected()
        {
            if (!_canDrawGizmo || alwaysShowGizmo) return;
            DrawGizmo();
        }


        private void DrawGizmo()
        {
            Gizmos.matrix = transform.localToWorldMatrix;

            Gizmos.color = _gizmoInteractCubeColor;
            Gizmos.DrawCube(_triggerCollider.center, _triggerCollider.size);

            Gizmos.color = Color.red;
            Gizmos.DrawLine(chairEdgeTransform.localPosition + Vector3.left * _triggerCollider.size.x * 0.5f, chairEdgeTransform.localPosition + Vector3.right * _triggerCollider.size.x * 0.5f);

            Gizmos.color = _gizmoChairEdgeCenterColor;
            Gizmos.DrawCube(chairEdgeTransform.localPosition, _gizmoChairEdgeCenterCubeSize);
        }
        #endif


        // References
        [SerializeField] private VRCStation vRCStation;

        private Transform _stationEnterTransform;

        [SerializeField] private Transform chairEdgeTransform;

        private Vector3 _chairEdgeLocalPosition; // Cache for performance, the local position should never change


        private VRCPlayerApi _localPlayer;

        private readonly Vector3 _vector3Zero = Vector3.zero; // Cache for performance


        // Internal states
        private bool _isStationOccupied;
        private VRCPlayerApi _stationedPlayer;


        private bool _isExiting;


        // Option for logging
        [SerializeField] private bool isLogging = true;


        // Variables for calibration

        #region Variables for calibration
        private int _calibrationCount;
        private int _passedCalibrationCount;

        private bool _isCalibrationEnabled;
        #endregion

        // Variables for networking

        #region Variables for networking
        [UdonSynced] private Vector3 _ownerFinalLocalPos;
        #endregion

        // Variables for vector calculation

        #region Variables for vector calculation
        // Experimentally these ratios should look natural enough on most avatars, sometimes with a bit of thigh squish.
        private const float TargetPosAlongThighLength = 1f / 6f; // Ratio of thigh length to move back from knee (how far is chair edge moved in along the thigh length)

        private const float TargetPosBelowThighLength = 1f / 12f; // Ratio of;thigh length to thigh thickness radius (how far is chair edge under the thigh vertically);


        private Vector3 _vectorToTargetPos;

        private Vector3 _kneeUpperBackPos;
        #endregion

        // Variables for smooth adjust

        #region Variables for smooth adjust
        private Vector3 _smoothAdjustCurrentPos;
        private Vector3 _smoothAdjustTargetPos;
        private bool _isSmoothAdjustEnabled;
        private float _smoothAdjustTimeoutTimer;
        private Vector3 _smoothAdjustVelocity;
        #endregion


        private void Start()
        {
            // Checking for setup errors

            // If somehow the station was unassigned in editor, try to point to the station on the same GameObject, if it errors then nothing is lost since it would be non-functional anyway.
            if (!vRCStation) vRCStation = GetComponent<VRCStation>();

            // Use the station enter from the station
            _stationEnterTransform = vRCStation.stationEnterPlayerLocation;

            // If somehow the chair edge was unassigned in editor, use the station itself as reference point.
            if (!chairEdgeTransform) chairEdgeTransform = transform;

            // Making sure station enter and chair edge are in the same local space.
            if (chairEdgeTransform.parent != _stationEnterTransform.parent) _stationEnterTransform.SetParent(chairEdgeTransform.parent, false);

            // Cache chair edge local position for performance
            _chairEdgeLocalPosition = chairEdgeTransform.localPosition;

            // Reset rotations
            // Chair edge and station enter must always face forward in local space because calibration is always aligned to prefab local space axes.
            chairEdgeTransform.localRotation = Quaternion.identity;
            _stationEnterTransform.localRotation = Quaternion.identity;

            // Cache local player for performance
            _localPlayer = Networking.LocalPlayer;

            // Store an RGB color specific to this chair instance for debug log purposes.
            _instanceColorHex = GetHexColor((float)new Random(GetInstanceID()).NextDouble());

            // Store an RGB color specific to this chair's parent for debug log purposes.
            if (!transform.parent)
            {
                // If AutoPosChair is disassembled or without a parent GameObject for any reason, return null.
                _parentNameForLog = $"<color=#{_instanceColorHex}>Parent: null (this is a root GameObject)</color>";
            }
            else
            {
                // If AutoPosChair is inside a prefab or a parent, return its name.
                _parentNameForLog = $"<color=#{GetHexColor((float)new Random(transform.parent.GetInstanceID()).NextDouble())}>Parent: {transform.parent.name}</color>";
            }
        }


        public override void Interact()
        {
            if (_isStationOccupied) return;

            _localPlayer.UseAttachedStation();
        }


        public override void OnStationEntered(VRCPlayerApi player)
        {
            if (!Utilities.IsValid(player)) return;

            // If somehow the same player enters without exiting (e.g. during exit confirmation period triggered by avatar change) then log as re-entering.
            if (player == _stationedPlayer) Log("Re-entering without exiting");

            // Setup
            _isStationOccupied = true;
            _stationedPlayer = player;
            _isExiting = false;
            UpdatePlayerNameForLog();

            Log("Entered");


            // Make first guess for initial position.
            // For future reference: In the default avatar sitting animation, the first 0.2 s is standing still, followed by 0.5 s of sitting down.
            // Initial position is at a notional floor position in front of the chair, at a scaled distance from chair edge using avatar height. This is to maintain the proportions of a nominal player capsule size (165 cm) sitting on a typical chair 0.5 m in height regardless of player avatar height.
            // stationEnterTransform is constrained to the X = 0 plane of the chair edge for calibration to work. Adjustments are only in the Y and Z axis so that the avatar is centered to the chair and not sitting left or right.
            _stationEnterTransform.localPosition = Vector3.LerpUnclamped(_chairEdgeLocalPosition, _chairEdgeLocalPosition + new Vector3(0f, -0.5f, 0.2f), _stationedPlayer.GetAvatarEyeHeightAsMeters() / 1.65f / chairEdgeTransform.lossyScale.y);


            StartCalibration();
        }


        private void StartCalibration()
        {
            if (!Utilities.IsValid(_stationedPlayer)) return;


            // Reset counters whenever a new calibration is requested.
            _calibrationCount = 0;
            _passedCalibrationCount = 0;

            // If owner synced their final pos, skip calibration and just use their data.
            if (!_stationedPlayer.isLocal && _ownerFinalLocalPos != _vector3Zero)
            {
                StopCalibration();
                StartSmoothAdjust(_ownerFinalLocalPos);
                return;
            }

            // If restarting during calibration, no need to start a new loop. Let the old loop continue looping with the reset counters.
            if (_isCalibrationEnabled)
            {
                Log("Calibration restarting");
                return;
            }

            Log("Calibration started");

            // Starting calibration loops
            _isCalibrationEnabled = true;
            SendCustomEventDelayedFrames("Calibrate", 0, EventTiming.PostLateUpdate);
        }


        // Checks if the avatar has leg bones, and if they are within reasonable proportions.
        private bool CheckIfHumanoid() => _stationedPlayer.GetBonePosition(HumanBodyBones.LeftUpperLeg) != _vector3Zero && _stationedPlayer.GetBonePosition(HumanBodyBones.RightUpperLeg) != _vector3Zero && Vector3.Distance(_stationedPlayer.GetBonePosition(HumanBodyBones.LeftUpperLeg), _stationedPlayer.GetBonePosition(HumanBodyBones.LeftLowerLeg)) >= 0.001f && Vector3.Distance(_stationedPlayer.GetBonePosition(HumanBodyBones.RightUpperLeg), _stationedPlayer.GetBonePosition(HumanBodyBones.RightLowerLeg)) >= 0.001f && chairEdgeTransform.parent.TransformVector(_vectorToTargetPos).magnitude < 5;


        // Checks if the station position has reached the target pos within an acceptable distance.
        private bool CheckIfWithinPassingTolerance() => chairEdgeTransform.parent.TransformVector(_vectorToTargetPos).magnitude < 0.005f;


        // Calibration loops
        public void Calibrate()
        {
            // Hard coding calibration interval timing
            const float calibrationHz = 8;
            const float calibrationTimeoutDuration = 8;

            const float calibrationInterval = 1 / calibrationHz;
            const float calibrationTimeoutCount = calibrationHz * calibrationTimeoutDuration;
            const float calibrationPassingTime = calibrationTimeoutDuration / 8;
            const float calibrationPassingCounts = calibrationHz * calibrationPassingTime;

            if (!_isCalibrationEnabled) return;

            if (!_isStationOccupied)
            {
                Log("Station is not occupied");
                StopCalibration();
                return;
            }

            if (!Utilities.IsValid(_stationedPlayer))
            {
                Log("Stationed player is null");
                StopCalibration();
                return;
            }


            // Cache the required movement vector in local space for this loop.
            _vectorToTargetPos = CalculateVectorToTargetPos();

            // Check if humanoid, if not, use the fall back calculation method directly.
            if (!CheckIfHumanoid())
            {
                Log("Non-humanoid proportions detected, using fall back method");
                StartSmoothAdjust(CalculateFallBackFinalPos());
                StopCalibration();
                return;
            }

            // Check if target is reached within a tolerance range, and finish calibration if passes consecutively for 1 s (1/8 of the timeout duration).
            if (CheckIfWithinPassingTolerance())
            {
                _passedCalibrationCount++;
                if (_passedCalibrationCount >= calibrationPassingCounts)
                {
                    Log($"Calibration success, passed {calibrationPassingCounts} times in a row");
                    StopCalibration();
                    return;
                }
            }
            else
            {
                _passedCalibrationCount = 0;
            }

            // Calculate the target pos and pass it to smooth adjust.
            StartSmoothAdjust(_stationEnterTransform.localPosition + _vectorToTargetPos);

            // Count completed calibrations, prepare for next calibration if still within limit.
            // Default 8 hz * 8 seconds limit = 64 counts
            _calibrationCount++;
            if (_calibrationCount > calibrationTimeoutCount)
            {
                Log($"Max calibration count reached");
                StopCalibration();
                return;
            }

            // Send delayed event for the next loop.
            // Default calibration frequency 8 hz (0.125 s interval).
            // Frequency should balance between perceptible transitions and performance. Note that this is just a measuring rate to guide the smooth adjust, so it doesn't need to happen every frame.
            // At above 0.2 seconds intervals it starts to become obvious. At 0.5 seconds it becomes very obvious.
            SendCustomEventDelayedSeconds("Calibrate", calibrationInterval, EventTiming.PostLateUpdate);
        }


        // Calculates the local space vector the station has to move so that the knee upper back position will reach chair edge position.
        // By design, the chair edge and station enter need to be in the same local space i.e. have the same parent GameObject, to avoid jumping between local spaces for performance reasons.
        // The knee upper back position is above the knee at the back side of the thigh, estimated based on upper leg length.
        // Note that some avatars can have their bone rotations set up differently, therefore bone rotations cannot be a good reference for front/back direction for the offset. Instead we are simply offseting in the chair local space.
        // X is always zero because we don't want to move the player sideways.
        // It also works for the cross-legged sitting pose of some avatars, the leg with the lower knee will be chosen for calibration.
        // It also works for uniformly scaled stations. (Non-uniformly scaled chairs will behave unpredictably because Unity lossy scale is lossy.)
        private Vector3 CalculateVectorToTargetPos()
        {
            // Calculate upper back knee pos based on each leg.
            Vector3 leftLegkneeUpperBackPos = CalculateKneeUpperBackPosForLeg(_stationedPlayer.GetBonePosition(HumanBodyBones.LeftUpperLeg), _stationedPlayer.GetBonePosition(HumanBodyBones.LeftLowerLeg));

            Vector3 rightLegkneeUpperBackPos = CalculateKneeUpperBackPosForLeg(_stationedPlayer.GetBonePosition(HumanBodyBones.RightUpperLeg), _stationedPlayer.GetBonePosition(HumanBodyBones.RightLowerLeg));

            // Decide which knee to use based on which one is the lower one in cross-legged sitting.
            _kneeUpperBackPos = leftLegkneeUpperBackPos.y <= rightLegkneeUpperBackPos.y ? leftLegkneeUpperBackPos : rightLegkneeUpperBackPos;
            _kneeUpperBackPos.x = 0;

            // Return the required movement vector in local space.
            return _chairEdgeLocalPosition - _kneeUpperBackPos;
        }

        // Calculates the visually convincing spot on each leg for resting on the chair edge, in local space.
        private Vector3 CalculateKneeUpperBackPosForLeg(Vector3 upperLegPos, Vector3 lowerLegPos)
        {
            Vector3 upperLegVector = lowerLegPos - upperLegPos;

            return chairEdgeTransform.parent.InverseTransformPoint(lowerLegPos - chairEdgeTransform.forward * upperLegVector.magnitude * TargetPosAlongThighLength - chairEdgeTransform.up * upperLegVector.magnitude * TargetPosBelowThighLength);
        }


        // For non-humanoid avatars, the position is calculated based on the player's avatar eye height setting to estimate how "big" the avatar is relative to the default player capsule size.
        // X and Y are unaffected because we are simply making the avatar stand on the chair with an offset from the front edge. The bigger the avatar, the further back it moves. Experimentally this looks fine because the non-humanoid avatars do not really have a sitting posture.
        // Note this calculation is in local space.
        // The ratio used for Z offset is based on the radius-to-height ratio of the VRC default player capsule size of 165 cm tall and 40 cm diameter, i.e. 165 * ~0.121212 = 20 cm offset.
        private Vector3 CalculateFallBackFinalPos()
        {
            const float radiusRatio = 20f / 165f;
            return new Vector3(_chairEdgeLocalPosition.x, _chairEdgeLocalPosition.y, _chairEdgeLocalPosition.z - _stationedPlayer.GetAvatarEyeHeightAsMeters() * radiusRatio / chairEdgeTransform.lossyScale.z);
        }


        private void StopCalibration()
        {
            _isCalibrationEnabled = false;

            Log("Calibration stopped");
            _calibrationCount = 0;
            _passedCalibrationCount = 0;

            // Sync final pos if you are the one sitting down, sync the final position to other players.
            // If not calibrating, probably has the final position.
            if (!Utilities.IsValid(_stationedPlayer) || !_stationedPlayer.isLocal || _isCalibrationEnabled) return;
            if (!Networking.IsOwner(gameObject)) Networking.SetOwner(_localPlayer, gameObject);
            _ownerFinalLocalPos = _smoothAdjustTargetPos;
            RequestSerialization();
        }


        // If you are not the owner, stop calibrating and adjust the position to where the owner thinks it should be.
        public override void OnDeserialization()
        {
            if (!Utilities.IsValid(_stationedPlayer) || _stationedPlayer.isLocal) return;

            Log("Received calibration result from remote player");
            StopCalibration();
            StartSmoothAdjust(_ownerFinalLocalPos);
        }


        // Moves station to the target pos with SpringDamp every frame for a specific duration
        private void StartSmoothAdjust(Vector3 targetPos)
        {
            // Refresh target pos and timer
            // Timer default 1 second
            _smoothAdjustTargetPos = targetPos;
            _smoothAdjustTimeoutTimer = 1;

            // If already adjusting, let the old loop continue with the reset timer.
            if (_isSmoothAdjustEnabled) return;

            // Start smooth adjust loops
            _smoothAdjustCurrentPos = _stationEnterTransform.localPosition; // Only setup current pos when freshly starting
            _isSmoothAdjustEnabled = true;
            SendCustomEventDelayedFrames("SmoothAdjust", 0, EventTiming.PostLateUpdate);
        }

        public void SmoothAdjust()
        {
            if (!_isSmoothAdjustEnabled)
            {
                StopSmoothAdjust();
                return;
            }

            // Jump to target directly if timer reached
            if (_smoothAdjustTimeoutTimer <= 0)
            {
                _stationEnterTransform.localPosition = _smoothAdjustTargetPos;

                StopSmoothAdjust();
                return;
            }

            // Move station
            _smoothAdjustCurrentPos = SpringDampVector3(_smoothAdjustCurrentPos, _smoothAdjustTargetPos, ref _smoothAdjustVelocity, Time.smoothDeltaTime, 4 * Mathf.PI); // Keeping current position in a variable to minimize externs
            _stationEnterTransform.localPosition = _smoothAdjustCurrentPos;

            // Progress the timer
            _smoothAdjustTimeoutTimer -= Time.deltaTime;

            // Loop in the next frame
            SendCustomEventDelayedFrames("SmoothAdjust", 1, EventTiming.PostLateUpdate);
        }

        private void StopSmoothAdjust()
        {
            _smoothAdjustCurrentPos = _stationEnterTransform.localPosition;
            _smoothAdjustTargetPos = _smoothAdjustCurrentPos;
            _smoothAdjustVelocity = _vector3Zero;
            _isSmoothAdjustEnabled = false;
        }


        // Calibrate again if height is changed.
        public override void OnAvatarEyeHeightChanged(VRCPlayerApi player, float prevEyeHeightAsMeters)
        {
            if (player != _stationedPlayer) return;
            Log("Avatar eye height changed");
            StartCalibration();
        }

        // No need to do OnAvatarChanged, because it already re-enters the station automatically. 
        public override void OnAvatarChanged(VRCPlayerApi player)
        {
            if (player != _stationedPlayer) return;
            Log("Avatar changed");
        }

        // No need to do OnPlayerRespawn either.
        public override void OnPlayerRespawn(VRCPlayerApi player)
        {
            if (player != _stationedPlayer) return;
            Log("Player respawned");
        }

        // Patching player leaving instance not triggering exit.
        public override void OnPlayerLeft(VRCPlayerApi player)
        {
            if (player != _stationedPlayer) return;
            Log("Player has left the instance");
            OnStationExited(player);
        }


        // To avoid the situation of changing avatar leading to entering and exiting quickly and resetting variables unintentionally, allow an exit confirmation period to check for quick re-entering without resetting.
        // Default 0.5 seconds.
        public override void OnStationExited(VRCPlayerApi player)
        {
            _isExiting = true;
            Log("Exiting...");
            SendCustomEventDelayedSeconds("Reset", 0.5f, EventTiming.Update);
        }


        public void Reset()
        {
            // If the station is re-entered during the confirmation period, _isExiting will be false, and the reset will not go ahead.
            if (!_isExiting) return;

            // If after 0.5 s is till exiting, proceed and reset to false.
            _isExiting = false;

            Log("Exited");

            _isStationOccupied = false;
            _stationedPlayer = null;
            _ownerFinalLocalPos = _vector3Zero;

            // Stop calibration loops
            if (_isCalibrationEnabled) StopCalibration();

            // Stop SmoothAdjust loops
            if (_isSmoothAdjustEnabled) StopSmoothAdjust();

            // Clear stored player name
            UpdatePlayerNameForLog();
        }


        #region Debug logging
        private string _instanceColorHex;
        private string _parentNameForLog;
        private string _playerNameForLog;

        private void Log(string message)
        {
            if (!isLogging) return;

            Debug.Log($"[<color=#{_instanceColorHex}>AutoPosChair</color>] {_parentNameForLog} {_playerNameForLog} {message}");
        }

        private static string GetHexColor(float hue, float saturation = 0.5f, float brightness = 0.875f)
        {
            Color32 col32 = Color.HSVToRGB(hue, saturation, brightness);
            return $"{col32.r:X2}{col32.g:X2}{col32.b:X2}";
        }


        private void UpdatePlayerNameForLog()
        {
            if (!Utilities.IsValid(_stationedPlayer))
            {
                _playerNameForLog = $"<color=#808080>Player: null</color>";
            }
            else
            {
                _playerNameForLog = $"<color=#{GetHexColor((float)new Random(_stationedPlayer.playerId).NextDouble())}>Player: {_stationedPlayer.displayName}</color>";
            }
        }
        #endregion


        #region SpringDamp
        // SpringDamp codes from github.com/UzerTekton/udonsharp-springdamp

        // Udon SmoothDamp is jank and cannot work as intended (there is a canny bug report), so we are using our own better smoothing method.

        // Smoothly interpolates a value toward a target using a damped harmonic oscillator.
        // Designed to mimic Unity's Mathf.SmoothDamp, but supports more precise control over damping.
        // 
        // Optimized for Udon usage by minimizing local variables, inlining single-use calculations,
        // and using Unity’s Mathf functions for best performance and accuracy within Udon’s constraints.
        // 
        // Parameters:
        //   current         - Current value
        //   target          - Target value
        //   currentVelocity - Reference to velocity (persistent across frames)
        //   deltaTime       - Time step (use Time.smoothDeltaTime for stable results)
        //   omega0          - Natural frequency in rad/s (higher = faster response).
        //                    Note: omega0 relates to frequency in Hertz (cycles per second) as:
        //                      frequency (Hz) = omega0 / (2 * π) ≈ omega0 / 6.283
        //                    Common omega0 values for typical frequencies:
        //                      1 Hz   -> omega0 ≈ 6.283
        //                      5 Hz   -> omega0 ≈ 31.416
        //                      8 Hz   -> omega0 ≈ 50      (default in this method)
        //                      10 Hz  -> omega0 ≈ 62.832
        //                      30 Hz  -> omega0 ≈ 188.496
        //   zeta            - Damping ratio (dimensionless), controls the system's damping behavior:
        //                      < 0        -> Unstable (negative damping, system diverges)
        //                      0          -> Undamped (pure oscillation, no decay)
        //                      0 < zeta < 1 -> Underdamped (oscillatory with decay):
        //                           ~0.1    -> Lightly damped, lots of oscillation before settling
        //                           ~0.25   -> Moderately damped, noticeable oscillations with faster decay
        //                           ~0.5    -> Heavily damped, fewer oscillations, smoother approach (this is default)
        //                      1          -> Critically damped (fastest return without overshoot)
        //                      > 1        -> Overdamped (no oscillations, slower smooth return)
        private static float SpringDamp(float current, float target, ref float currentVelocity, float deltaTime, float omega0 = 50f, float zeta = 0.5f)
        {
            float x = current - target;
            float v = currentVelocity;
            float omegaZeta = omega0 * zeta;
            float zetaSq = zeta * zeta;

            if (zeta < 1f)
            {
                // Underdamped
                float omegaD = omega0 * Mathf.Sqrt(1f - zetaSq);
                float temp = (v + omegaZeta * x) / omegaD;
                float exp = Mathf.Exp(-omegaZeta * deltaTime);
                float angle = omegaD * deltaTime;
                float sin = Mathf.Sin(angle);
                float cos = Mathf.Cos(angle);

                float newX = exp * (x * cos + temp * sin);
                float newV = exp * (-omegaZeta * newX + temp * omegaD * cos - x * omegaD * sin);

                currentVelocity = newV;
                return target + newX;
            }
            else if (zeta > 1f)
            {
                // Overdamped
                float sqrtTerm = Mathf.Sqrt(zetaSq - 1f);
                float r1 = -omega0 * (zeta - sqrtTerm);
                float r2 = -omega0 * (zeta + sqrtTerm);

                float c2 = (v - r1 * x) / (r2 - r1);
                float c1 = x - c2;

                float e1 = Mathf.Exp(r1 * deltaTime);
                float e2 = Mathf.Exp(r2 * deltaTime);

                float newX = c1 * e1 + c2 * e2;
                float newV = c1 * r1 * e1 + c2 * r2 * e2;

                currentVelocity = newV;
                return target + newX;
            }
            else
            {
                // Critically damped
                float exp = Mathf.Exp(-omega0 * deltaTime);
                float temp = v + omega0 * x;

                float newX = exp * (x + temp * deltaTime);
                float newV = exp * (v - omega0 * temp * deltaTime);

                currentVelocity = newV;
                return target + newX;
            }
        }

        // Gradually changes an angle given in degrees towards a desired goal angle over time using SpringDamp.
        // public static float SpringDampAngle(float current, float target, ref float currentVelocity, float deltaTime, float omega0 = 50f, float zeta = 0.5f)
        // {
        //     target = current + Mathf.DeltaAngle(current, target);
        //     return SpringDamp(current, target, ref currentVelocity, deltaTime, omega0, zeta);
        // }

        // Gradually changes a vector towards a desired goal over time using SpringDamp.
        private static Vector3 SpringDampVector3(Vector3 current, Vector3 target, ref Vector3 currentVelocity, float deltaTime, float omega0 = 50f, float zeta = 0.5f)
        {
            // current.x = SpringDamp(current.x, target.x, ref currentVelocity.x, deltaTime, omega0, zeta);
            // Forcing x to be 0 in our use case
            current.x = 0;
            current.y = SpringDamp(current.y, target.y, ref currentVelocity.y, deltaTime, omega0, zeta);
            current.z = SpringDamp(current.z, target.z, ref currentVelocity.z, deltaTime, omega0, zeta);
            return current;
        }
        #endregion
    }


    #region Custom Inspector
    #if !COMPILER_UDONSHARP && UNITY_EDITOR

    [CustomEditor(typeof(AutoPosChair))]
    public class CustomInspectorEditor : Editor
    {
        private AutoPosChair _autoPosChair;
        private SerializedProperty vRCStation;
        private SerializedProperty chairEdgeTransform;
        private SerializedProperty isLogging;
        private SerializedProperty alwaysShowGizmo;

        private static bool IsShowingUdonSettings
        {
            get => SessionState.GetBool("isAutoPosChairEditorShowingUdonSettings", false);
            set => SessionState.SetBool("isAutoPosChairEditorShowingUdonSettings", value);
        }

        private void OnEnable()
        {
            _autoPosChair = (AutoPosChair)target;

            vRCStation = serializedObject.FindProperty("vRCStation");
            chairEdgeTransform = serializedObject.FindProperty("chairEdgeTransform");
            isLogging = serializedObject.FindProperty("isLogging");
            alwaysShowGizmo = serializedObject.FindProperty("alwaysShowGizmo");
        }


        private GUIContent _vRCStationLabel = new GUIContent("VRC Station", "The VRC Station to be adjusted.");

        private GUIContent _chairEdgeTransformLabel = new GUIContent("Chair edge Transform", "The Transform for locating the front edge of the seating surface.");

        private GUIContent _isLoggingLabel = new GUIContent("Enable debug logging", "If enabled, this AutoPosChair will report its real-time status to the in-game debug log.\nYou may want to disable this if you want to hide this information from other players e.g. in a game world.");

        private GUIContent _alwaysShowGizmoLabel = new GUIContent("Always show gizmo (editor only)", "If enabled, the gizmo (the highlighted interaction cube) will always be shown in the editor scene view.\nOtherwise, it will only show when the GameObject is selected.\nThis option has no effect on the uploaded prefab in-game.");


        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            // Title
            EditorGUILayout.LabelField("AutoPosChair 2.0.0 by Uzer Tekton", EditorStyles.boldLabel);

            // Checking trigger Collider
            if (!_autoPosChair.TryGetComponent<Collider>(out Collider interactTriggerCollider))
            {
                EditorGUILayout.HelpBox($"Trigger Collider is missing. A trigger Collider on the same GameObject is required for interaction.\nAdd one automatically?", MessageType.Warning);

                if (GUILayout.Button("Add a trigger Collider to this GameObject with the default settings"))
                {
                    BoxCollider triggerCollider = _autoPosChair.gameObject.AddComponent<BoxCollider>();
                    triggerCollider.isTrigger = true;
                    triggerCollider.center = Vector3.up * 0.05f;
                    triggerCollider.size = new Vector3(0.4f, 0.1f, 0.4f);
                }
            }
            else
            {
                if (!interactTriggerCollider.isTrigger)
                {
                    EditorGUILayout.HelpBox($"The Collider is not set as a trigger. A trigger Collider on the same GameObject is required for interaction.\nSet it to a trigger automatically?", MessageType.Warning);


                    if (GUILayout.Button("Set the Collider on this GameObject as a trigger Collider"))
                    {
                        interactTriggerCollider.isTrigger = true;
                    }
                }
            }

            // Checking the layer this is on
            if (_autoPosChair.gameObject.layer != 8)
            {
                EditorGUILayout.HelpBox($"It is recommended to put this GameObject on the Interactive layer so that stickers cannot be put onto the invisible collider.\nFix this automatically?", MessageType.Warning);


                if (GUILayout.Button("Put this on the Interactive layer"))
                {
                    _autoPosChair.gameObject.layer = 8;
                }
            }


            // Fields
            EditorGUILayout.PropertyField(vRCStation, _vRCStationLabel);
            if (!vRCStation.objectReferenceValue)
            {
                const string stationWarning = "VRC Station reference is missing. Assign a VRC Station to be calibrated.";

                if (_autoPosChair.TryGetComponent<VRCStation>(out VRCStation vRCStationOnSameGO))
                {
                    EditorGUILayout.HelpBox($"{stationWarning}\nFound a VRC Station attached to this GameObject, is this the station you want to adjust?", MessageType.Warning);

                    if (GUILayout.Button("Use the VRC Station found on this GameObject"))
                    {
                        vRCStation.objectReferenceValue = vRCStationOnSameGO;
                    }
                }
                else
                {
                    EditorGUILayout.HelpBox($"{stationWarning}\nAdd one automatically?", MessageType.Warning);

                    if (GUILayout.Button("Add a new VRC Station to this GameObject"))
                    {
                        vRCStation.objectReferenceValue = _autoPosChair.gameObject.AddComponent<VRC.SDK3.Components.VRCStation>();
                    }
                }
            }
            else
            {
                VRCStation vRCStationReferenced = (VRCStation)vRCStation.objectReferenceValue;
                if (!vRCStationReferenced.stationEnterPlayerLocation)
                {
                    const string stationEnterWarning = "Station Enter Location is missing on the VRC Station. This needs to be a separate GameObject for the calibration to work.";
                    if (_autoPosChair.transform.Find("StationEnter"))
                    {
                        EditorGUILayout.HelpBox($"{stationEnterWarning}\nFound a child GameObject named \"StationEnter\", use it?", MessageType.Warning);
                        if (GUILayout.Button("Use the found StationEnter"))
                        {
                            vRCStationReferenced.stationEnterPlayerLocation = _autoPosChair.transform.Find("StationEnter");
                        }
                    }
                    else
                    {
                        EditorGUILayout.HelpBox($"{stationEnterWarning}\nCreate one automatically?", MessageType.Warning);
                        if (GUILayout.Button("Create a new StationEnter with the default settings"))
                        {
                            GameObject newStationEnter = new GameObject();
                            newStationEnter.name = "StationEnter";
                            newStationEnter.transform.parent = _autoPosChair.transform;
                            newStationEnter.transform.localPosition = Vector3.zero;
                            newStationEnter.transform.localRotation = Quaternion.identity;
                            newStationEnter.transform.localScale = Vector3.one;
                            vRCStationReferenced.stationEnterPlayerLocation = newStationEnter.transform;
                        }
                    }
                }

                if (!vRCStationReferenced.stationExitPlayerLocation)
                {
                    const string stationExitWarning = "Station Exit Location is missing on the VRC Station.";
                    if (_autoPosChair.transform.Find("StationExit"))
                    {
                        EditorGUILayout.HelpBox($"{stationExitWarning}\nFound a child GameObject named \"StationExit\", use it?", MessageType.Warning);
                        if (GUILayout.Button("Use the found StationExit"))
                        {
                            vRCStationReferenced.stationExitPlayerLocation = _autoPosChair.transform.Find("StationExit");
                        }
                    }
                    else
                    {
                        EditorGUILayout.HelpBox($"{stationExitWarning}\nCreate one automatically?", MessageType.Warning);
                        if (GUILayout.Button("Create a new StationExit with the default settings"))
                        {
                            GameObject newStationExit = new GameObject();
                            newStationExit.name = "StationExit";
                            newStationExit.transform.parent = _autoPosChair.transform;
                            newStationExit.transform.localPosition = new Vector3(0, -0.5f, 0.4f);
                            newStationExit.transform.localRotation = Quaternion.identity;
                            newStationExit.transform.localScale = Vector3.one;
                            vRCStationReferenced.stationExitPlayerLocation = newStationExit.transform;
                        }
                    }
                }
            }


            EditorGUILayout.PropertyField(chairEdgeTransform, _chairEdgeTransformLabel);

            if (!chairEdgeTransform.objectReferenceValue)
            {
                const string chairWarning = "Chair edge Transform reference is missing.  Assign a Transform placed at the middle point of the front edge of the seating surface, with Z pointing forward.";

                if (_autoPosChair.transform.Find("ChairEdge"))

                {
                    EditorGUILayout.HelpBox($"{chairWarning}\nFound a child GameObject named \"ChairEdge\", use it?", MessageType.Warning);
                    if (GUILayout.Button("Use the found chair edge Transform"))
                    {
                        chairEdgeTransform.objectReferenceValue = _autoPosChair.transform.Find("ChairEdge");
                    }
                }

                else
                {
                    EditorGUILayout.HelpBox($"{chairWarning}\nCreate one automatically?", MessageType.Warning);
                    if (GUILayout.Button("Create a chair edge Transform at the default position"))
                    {
                        GameObject newChairEdge = new GameObject();
                        newChairEdge.name = "ChairEdge";
                        newChairEdge.transform.parent = _autoPosChair.transform;
                        newChairEdge.transform.localPosition = Vector3.forward * 0.2f;
                        newChairEdge.transform.localRotation = Quaternion.identity;
                        newChairEdge.transform.localScale = Vector3.one;
                        chairEdgeTransform.objectReferenceValue = newChairEdge.transform;
                    }
                }
            }

            EditorGUILayout.PropertyField(isLogging, _isLoggingLabel);
            EditorGUILayout.PropertyField(alwaysShowGizmo, _alwaysShowGizmoLabel);

            // Udon settings
            IsShowingUdonSettings = EditorGUILayout.Foldout(IsShowingUdonSettings, "Udon settings");
            if (IsShowingUdonSettings)
            {
                if (UdonSharpGUI.DrawProgramSource(target)) return;
                UdonSharpGUI.DrawCompileErrorTextArea();
                UdonSharpGUI.DrawSyncSettings(target);
                UdonSharpGUI.DrawUtilities(target);

                EditorGUILayout.LabelField("Interaction", EditorStyles.boldLabel);
                EditorGUI.indentLevel++;
                UdonSharpGUI.DrawInteractSettings(target);
                EditorGUI.indentLevel--;
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
    #endif
    #endregion
}
