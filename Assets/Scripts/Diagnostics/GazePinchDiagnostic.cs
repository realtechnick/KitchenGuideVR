// GazePinchDiagnostic.cs
// KitchenGuide diagnostic (2026-11-07): proves gaze aiming + pinch selection
// with NO virtual hand models — the user sees their real hands in passthrough;
// feedback lives on the target, per the project's no-hand-models design rule.
//
// Self-bootstrapping via RuntimeInitializeOnLoadMethod: needs NO scene wiring.
// Spawns a panel 1.5 m ahead of the user's head once tracking reports a settled,
// plausible head pose (v2 — v1 placed it at Start from an unconverged pose and
// it landed on the floor). Placement law: eye height, yaw-flattened forward.
// A long pinch (>= 0.8 s) started while NOT gazing at the panel SUMMONS it:
// the panel re-places in front of the current head pose. Hands are neither
// drawn nor hidden: the hand mesh renders depth-only, so the user's real
// hand occludes virtual content organically (see EnforceHandVisuals).
//   LEFT orb  = left hand tracked (cyan) / not tracked (grey) / no OVRHand found (red)
//   MID  orb  = pinch state (yellow while pinching)
//   RIGHT orb = right hand, same scheme as left
// Panel: slate = idle, bright blue = gaze hover, green = toggled ON by a pinch.
// Pinch while gazing at the panel = select (toggles it, logs "[GazePinch] SELECT").
// State changes + a 2 s heartbeat with raw pinch strengths go to the log
// (read with: metavr log). Controller trigger also counts as a pinch so the
// gaze half can be tested without hand data. Delete this file to remove it.
using UnityEngine;

public class GazePinchDiagnostic : MonoBehaviour
{
    private const string Tag = "[GazePinch]";
    private const float PinchThreshold = 0.7f;

    private static readonly Color PanelIdle    = new Color(0.16f, 0.28f, 0.45f);
    private static readonly Color PanelHover   = new Color(0.30f, 0.52f, 0.80f);
    private static readonly Color PanelOn      = new Color(0.15f, 0.65f, 0.30f);
    private static readonly Color OrbOff       = new Color(0.25f, 0.25f, 0.25f);
    private static readonly Color OrbTracked   = new Color(0.10f, 0.75f, 0.85f);
    private static readonly Color OrbPinch     = new Color(1.00f, 0.80f, 0.15f);
    private static readonly Color OrbNoHand    = new Color(0.70f, 0.15f, 0.15f);
    private static readonly Color ReticleIdle  = new Color(0.60f, 0.60f, 0.60f);
    private static readonly Color ReticleHover = new Color(1.00f, 1.00f, 1.00f);

    private Camera _cam;
    private OVRHand _leftHand;
    private OVRHand _rightHand;

    private GameObject _panel;
    private Collider _panelCollider;
    private Material _panelMat;
    private GameObject _reticle;
    private Material _reticleMat;
    private Material _orbLMat, _orbPinchMat, _orbRMat;

    private bool _panelOn;
    private bool _wasPinched;
    private bool _wasGazing;
    private float _logTimer;
    private int _selectCount;

    private GameObject _orbL, _orbPinch, _orbR;
    private bool _placed;
    private float _bootTime;
    private int _stableFrames;
    private Vector3 _lastHeadPos;
    private float _pinchStartTime;
    private bool _pinchFromPanel;
    private bool _summonFired;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        // RETIRED 2026-11-07: the diagnostic proved the interaction loop
        // (gaze, pinch, placement, occlusion) and handed off to
        // KitchenGuideApp, which carries the same proven core. This file
        // stays in the repo as reference; it no longer self-bootstraps.
        return;
#pragma warning disable CS0162
        if (FindAnyObjectByType<GazePinchDiagnostic>() != null) return;
        new GameObject("GazePinchDiagnostic").AddComponent<GazePinchDiagnostic>();
        Debug.Log(Tag + " diagnostic bootstrapped");
#pragma warning restore CS0162
    }

    private void Start()
    {
        _cam = Camera.main;
        if (_cam == null) _cam = FindAnyObjectByType<Camera>();
        if (_cam == null)
        {
            Debug.LogWarning(Tag + " no camera found; diagnostic idle.");
            return;
        }

        FindHands();
        _bootTime = Time.time;
        // If the user recenters the OS view, the tracking frame rotates under
        // world-locked content — re-place the panel in front of the new forward.
        OVRManager.display.RecenteredPose += OnRecenteredPose;
        Debug.Log(Tag + " waiting for a stable head pose before placing the panel...");
    }

    private void FindHands()
    {
        var rig = FindAnyObjectByType<OVRCameraRig>();
        if (rig != null)
        {
            if (_leftHand == null && rig.leftHandAnchor != null)
                _leftHand = rig.leftHandAnchor.GetComponentInChildren<OVRHand>(true);
            if (_rightHand == null && rig.rightHandAnchor != null)
                _rightHand = rig.rightHandAnchor.GetComponentInChildren<OVRHand>(true);
        }
        if (_leftHand == null || _rightHand == null)
        {
            var hands = FindObjectsByType<OVRHand>(FindObjectsInactive.Include);
            foreach (var h in hands)
            {
                if (_leftHand == null && h != _rightHand) _leftHand = h;
                else if (_rightHand == null && h != _leftHand) _rightHand = h;
            }
        }
        _rescanTimer = 1f; // LateUpdate enforcement picks the hands up next frame
        Debug.Log(Tag + " OVRHand found: left=" + (_leftHand != null) +
                  " right=" + (_rightHand != null) + " rig=" + (rig != null));
    }

    // Hand occlusion (Boss's direction, 2026-11-07): don't hide the tracked
    // hands and don't draw them either — render the hand mesh into the depth
    // buffer ONLY (HandOcclusionMaterial, which ships in Resources), so the
    // user's REAL hand, seen via passthrough, correctly masks virtual content
    // behind it. Organic depth instead of cartoon models — or no hands at all.
    //
    // Enforcement runs in LateUpdate, AFTER the SDK's own per-frame hand
    // updates: in SDK 207 the OVRMeshRenderer conductor builds/re-enables the
    // skinned hand renderer when the mesh becomes ready, which is AFTER any
    // Start-time pass — v4's one-shot swap (and v3.1's OVRMesh disable) never
    // touched the renderer that actually draws the gloves. A LateUpdate pass
    // has the last word before the frame renders, so there is nothing left
    // to fight and no flicker.
    private Material _occlusionMat;
    private bool _occlusionWarned;
    private float _rescanTimer;
    private Renderer[] _leftRenderers = new Renderer[0];
    private Renderer[] _rightRenderers = new Renderer[0];

    private void EnforceHandVisuals()
    {
        if (_leftHand == null && _rightHand == null) return;
        if (_occlusionMat == null)
            _occlusionMat = Resources.Load<Material>("HandOcclusionMaterial");

        _rescanTimer += Time.deltaTime;
        if (_rescanTimer >= 0.25f)
        {
            _rescanTimer = 0f;
            if (_leftHand != null)
                _leftRenderers = _leftHand.GetComponentsInChildren<Renderer>(true);
            if (_rightHand != null)
                _rightRenderers = _rightHand.GetComponentsInChildren<Renderer>(true);
        }
        ApplyToRenderers(_leftRenderers);
        ApplyToRenderers(_rightRenderers);
    }

    private void ApplyToRenderers(Renderer[] renderers)
    {
        foreach (var r in renderers)
        {
            if (r == null) continue;
            if (_occlusionMat != null)
            {
                if (r.sharedMaterial != _occlusionMat) r.sharedMaterial = _occlusionMat;
                if (!r.enabled) r.enabled = true;
            }
            else if (r.enabled) r.enabled = false;
        }
        if (_occlusionMat == null && !_occlusionWarned && renderers.Length > 0)
        {
            _occlusionWarned = true;
            Debug.LogWarning(Tag + " HandOcclusionMaterial missing — hands hidden instead of occluding");
        }
    }

    private static Material MakeMat(Color c)
    {
        Shader s = Shader.Find("Universal Render Pipeline/Unlit");
        if (s == null) s = Shader.Find("Unlit/Color");
        if (s == null) s = Shader.Find("Sprites/Default");
        var m = new Material(s);
        m.color = c;
        return m;
    }

    // Placement law (Boss's call, 2026-11-07): menus spawn from the user's ACTUAL
    // head pose — eye height, 1.5 m ahead, yaw-flattened so panels stay upright.
    // (2 m read as "about 8 feet" in-headset — too far for a working panel.)
    // Creates the test objects on first call; repositions them on later calls.
    private void PlacePanel()
    {
        Vector3 headPos = _cam.transform.position;
        Vector3 flatFwd = Vector3.ProjectOnPlane(_cam.transform.forward, Vector3.up);
        if (flatFwd.sqrMagnitude < 0.01f)
            flatFwd = Vector3.ProjectOnPlane(_cam.transform.right, Vector3.up);
        flatFwd.Normalize();
        Quaternion rot = Quaternion.LookRotation(flatFwd, Vector3.up);
        Vector3 panelPos = headPos + flatFwd * 1.5f;
        Vector3 right = rot * Vector3.right;
        Vector3 up = rot * Vector3.up;

        if (_panel == null)
        {
            _panelMat = MakeMat(PanelIdle);
            _panel = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _panel.name = "GazePanel";
            _panel.transform.localScale = new Vector3(0.55f, 0.38f, 0.03f);
            _panel.GetComponent<Renderer>().sharedMaterial = _panelMat;
            _panelCollider = _panel.GetComponent<Collider>();

            _orbLMat = MakeMat(OrbOff);
            _orbPinchMat = MakeMat(OrbOff);
            _orbRMat = MakeMat(OrbOff);
            _orbL = MakeOrb(Vector3.zero, _orbLMat, "OrbLeft");
            _orbPinch = MakeOrb(Vector3.zero, _orbPinchMat, "OrbPinch");
            _orbR = MakeOrb(Vector3.zero, _orbRMat, "OrbRight");

            _reticleMat = MakeMat(ReticleIdle);
            _reticle = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _reticle.name = "GazeReticle";
            Destroy(_reticle.GetComponent<Collider>());
            _reticle.GetComponent<Renderer>().sharedMaterial = _reticleMat;
            _reticle.transform.localScale = Vector3.one * 0.01f;
        }

        _panel.transform.position = panelPos;
        _panel.transform.rotation = rot;
        _orbL.transform.position = panelPos + right * -0.12f + up * -0.28f + flatFwd * 0.02f;
        _orbPinch.transform.position = panelPos + right * 0.00f + up * -0.28f + flatFwd * 0.02f;
        _orbR.transform.position = panelPos + right * 0.12f + up * -0.28f + flatFwd * 0.02f;
        _placed = true;
        Debug.Log(Tag + " panel placed at " + panelPos +
                  " (head height " + headPos.y.ToString("0.00") + " m)");
    }

    private void OnRecenteredPose()
    {
        Debug.Log(Tag + " OS recenter detected" + (_placed ? " — panel re-placed" : ""));
        if (_placed) PlacePanel();
    }

    private GameObject MakeOrb(Vector3 pos, Material mat, string name)
    {
        var orb = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        orb.name = name;
        orb.transform.position = pos;
        orb.transform.localScale = Vector3.one * 0.042f;
        Destroy(orb.GetComponent<Collider>());
        orb.GetComponent<Renderer>().sharedMaterial = mat;
        return orb;
    }

    private static bool HandTracked(OVRHand h)
    {
        return h != null && h.IsTracked;
    }

    private static float PinchStrength(OVRHand h)
    {
        if (h == null || !h.IsTracked) return 0f;
        return h.GetFingerPinchStrength(OVRHand.HandFinger.Index);
    }

    private static bool HandPinching(OVRHand h)
    {
        // SDK 207 note: OVRHand.IsFingerPinched no longer exists (compiler-verified
        // 2026-11-07). GetFingerPinchStrength is the surviving API; the old
        // IsFingerPinched was a threshold on this same value — derive it here.
        return h != null && h.IsTracked && PinchStrength(h) >= PinchThreshold;
    }

    private void LateUpdate()
    {
        EnforceHandVisuals();
    }

    private void Update()
    {
        if (_cam == null) return;

        if (!_placed)
        {
            // Wait for a settled, plausible head pose (tracking converged,
            // headset actually being worn) instead of placing at the origin.
            // 8 s timeout places anyway — the summon gesture can re-place.
            Vector3 hp = _cam.transform.position;
            bool plausible = hp.y > 0.7f && hp.y < 2.3f;
            bool settled = (hp - _lastHeadPos).sqrMagnitude < 0.0004f;
            _stableFrames = (plausible && settled) ? _stableFrames + 1 : 0;
            _lastHeadPos = hp;
            if (_stableFrames >= 30 || Time.time - _bootTime > 8.0f) PlacePanel();
            return;
        }

        // --- gaze ---
        var ray = new Ray(_cam.transform.position, _cam.transform.forward);
        bool gazing = _panelCollider.Raycast(ray, out RaycastHit hit, 10f);
        float dist = gazing ? hit.distance : 3.0f;
        _reticle.transform.position = _cam.transform.position + _cam.transform.forward * dist
                                      - _cam.transform.forward * 0.02f;
        float s = Mathf.Clamp(dist * 0.006f, 0.004f, 0.03f);
        _reticle.transform.localScale = Vector3.one * s;
        _reticleMat.color = gazing ? ReticleHover : ReticleIdle;
        if (gazing != _wasGazing)
        {
            Debug.Log(Tag + " gaze " + (gazing ? "ON panel" : "off panel"));
            _wasGazing = gazing;
        }

        // --- pinch (either hand; controller trigger as fallback) ---
        bool pinched = HandPinching(_leftHand) || HandPinching(_rightHand)
                       || OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger);
        if (pinched && !_wasPinched)
        {
            _pinchStartTime = Time.time;
            _pinchFromPanel = gazing;
            _summonFired = false;
            Debug.Log(Tag + " pinch DOWN (gazing=" + gazing + ")");
            if (gazing)
            {
                _panelOn = !_panelOn;
                _selectCount++;
                Debug.Log(Tag + " SELECT #" + _selectCount + " panelOn=" + _panelOn);
            }
        }
        // Long pinch (>= 0.8 s) started AWAY from the panel = summon:
        // re-place the panel in front of the current head pose.
        if (pinched && !_pinchFromPanel && !_summonFired && Time.time - _pinchStartTime >= 0.8f)
        {
            _summonFired = true;
            PlacePanel();
            Debug.Log(Tag + " SUMMON — panel re-placed at current head pose");
        }
        if (!pinched) _summonFired = false;
        _wasPinched = pinched;

        // --- feedback colors ---
        _panelMat.color = _panelOn ? PanelOn : (gazing ? PanelHover : PanelIdle);
        _orbLMat.color = _leftHand == null ? OrbNoHand
            : (HandTracked(_leftHand) ? OrbTracked : OrbOff);
        _orbRMat.color = _rightHand == null ? OrbNoHand
            : (HandTracked(_rightHand) ? OrbTracked : OrbOff);
        _orbPinchMat.color = pinched ? OrbPinch
            : ((_leftHand == null && _rightHand == null) ? OrbNoHand : OrbOff);

        // --- heartbeat for log-based diagnosis ---
        _logTimer += Time.deltaTime;
        if (_logTimer >= 2.0f)
        {
            _logTimer = 0f;
            if (_leftHand == null || _rightHand == null) FindHands();
            Debug.Log(Tag + " status L(tracked=" + HandTracked(_leftHand) +
                      ", pinch=" + PinchStrength(_leftHand).ToString("0.00") +
                      ") R(tracked=" + HandTracked(_rightHand) +
                      ", pinch=" + PinchStrength(_rightHand).ToString("0.00") +
                      ") selects=" + _selectCount);
        }
    }
}
