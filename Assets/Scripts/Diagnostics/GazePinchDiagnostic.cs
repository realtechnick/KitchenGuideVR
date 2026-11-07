// GazePinchDiagnostic.cs
// KitchenGuide diagnostic (2026-11-07): proves gaze aiming + pinch selection
// with NO virtual hand models — the user sees their real hands in passthrough;
// feedback lives on the target, per the project's no-hand-models design rule.
//
// Self-bootstrapping via RuntimeInitializeOnLoadMethod: needs NO scene wiring.
// Spawns a panel 2 m ahead of wherever the user is looking at app start, a gaze
// reticle, and three status orbs under the panel:
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

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (FindFirstObjectByType<GazePinchDiagnostic>() != null) return;
        new GameObject("GazePinchDiagnostic").AddComponent<GazePinchDiagnostic>();
        Debug.Log(Tag + " diagnostic bootstrapped");
    }

    private void Start()
    {
        _cam = Camera.main;
        if (_cam == null) _cam = FindFirstObjectByType<Camera>();
        if (_cam == null)
        {
            Debug.LogWarning(Tag + " no camera found; diagnostic idle.");
            return;
        }

        var rig = FindFirstObjectByType<OVRCameraRig>();
        if (rig != null)
        {
            if (rig.leftHandAnchor != null)
                _leftHand = rig.leftHandAnchor.GetComponentInChildren<OVRHand>(true);
            if (rig.rightHandAnchor != null)
                _rightHand = rig.rightHandAnchor.GetComponentInChildren<OVRHand>(true);
        }
        if (_leftHand == null || _rightHand == null)
        {
            var hands = FindObjectsByType<OVRHand>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var h in hands)
            {
                if (_leftHand == null) _leftHand = h;
                else if (_rightHand == null && h != _leftHand) _rightHand = h;
            }
        }
        Debug.Log(Tag + " OVRHand found: left=" + (_leftHand != null) +
                  " right=" + (_rightHand != null) + " rig=" + (rig != null));

        BuildTestRig();
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

    private void BuildTestRig()
    {
        Vector3 fwd = _cam.transform.forward;
        Vector3 panelPos = _cam.transform.position + fwd * 2.0f;
        Quaternion panelRot = Quaternion.LookRotation(fwd, Vector3.up);
        Vector3 right = panelRot * Vector3.right;
        Vector3 up = panelRot * Vector3.up;

        _panelMat = MakeMat(PanelIdle);
        _panel = GameObject.CreatePrimitive(PrimitiveType.Cube);
        _panel.name = "GazePanel";
        _panel.transform.position = panelPos;
        _panel.transform.rotation = panelRot;
        _panel.transform.localScale = new Vector3(0.55f, 0.38f, 0.03f);
        _panel.GetComponent<Renderer>().sharedMaterial = _panelMat;
        _panelCollider = _panel.GetComponent<Collider>();

        _orbLMat = MakeMat(OrbOff);
        _orbPinchMat = MakeMat(OrbOff);
        _orbRMat = MakeMat(OrbOff);
        MakeOrb(panelPos + right * -0.12f + up * -0.28f + fwd * 0.02f, _orbLMat, "OrbLeft");
        MakeOrb(panelPos + right *  0.00f + up * -0.28f + fwd * 0.02f, _orbPinchMat, "OrbPinch");
        MakeOrb(panelPos + right *  0.12f + up * -0.28f + fwd * 0.02f, _orbRMat, "OrbRight");

        _reticleMat = MakeMat(ReticleIdle);
        _reticle = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        _reticle.name = "GazeReticle";
        Destroy(_reticle.GetComponent<Collider>());
        _reticle.GetComponent<Renderer>().sharedMaterial = _reticleMat;
        _reticle.transform.localScale = Vector3.one * 0.01f;

        Debug.Log(Tag + " panel placed at " + panelPos);
    }

    private void MakeOrb(Vector3 pos, Material mat, string name)
    {
        var orb = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        orb.name = name;
        orb.transform.position = pos;
        orb.transform.localScale = Vector3.one * 0.042f;
        Destroy(orb.GetComponent<Collider>());
        orb.GetComponent<Renderer>().sharedMaterial = mat;
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
        return h != null && h.IsTracked && h.IsFingerPinched(OVRHand.HandFinger.Index);
    }

    private void Update()
    {
        if (_cam == null || _panel == null) return;

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
            Debug.Log(Tag + " pinch DOWN (gazing=" + gazing + ")");
            if (gazing)
            {
                _panelOn = !_panelOn;
                _selectCount++;
                Debug.Log(Tag + " SELECT #" + _selectCount + " panelOn=" + _panelOn);
            }
        }
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
            Debug.Log(Tag + " status L(tracked=" + HandTracked(_leftHand) +
                      ", pinch=" + PinchStrength(_leftHand).ToString("0.00") +
                      ") R(tracked=" + HandTracked(_rightHand) +
                      ", pinch=" + PinchStrength(_rightHand).ToString("0.00") +
                      ") selects=" + _selectCount);
        }
    }
}
