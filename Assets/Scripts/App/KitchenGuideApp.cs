// KitchenGuideApp.cs — KitchenGuide functional slice (Step A, 2026-11-07)
//
// The app foundation: the gaze-pinch diagnostic's PROVEN interaction core
// (settled-pose panel placement at 1.5 m, gaze hover, pinch select,
// long-pinch summon, recenter re-place, LateUpdate hand-occlusion
// enforcement) carrying a real cooking UI:
//   * world-space canvas panel per Meta's Quest UI conventions
//     (scale 0.001, TMP text, generous targets)
//   * a recipe step engine (Back / Next, pinch panel = Next)
//   * detection-aware steps (auto-advance when DetectionManager reports
//     the step's ingredient via CounterState — Step B wiring)
//   * floating world-locked timer chips with countdowns
// Self-bootstrapping like the diagnostic was: no scene wiring; delete the
// file to remove it. Custom code lives here (the app's brain); interaction
// plumbing conventions follow Meta's platform patterns.

using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>What the detection layer has seen on the counter. Step B's
/// DetectionManager writes here; the UI reads. Class names are COCO labels.</summary>
public static class CounterState
{
    public static readonly HashSet<string> Seen = new HashSet<string>();
}

public class KitchenGuideApp : MonoBehaviour
{
    private const string Tag = "[KitchenGuide]";
    private const float PinchThreshold = 0.7f;
    private const float SummonSeconds = 0.8f;
    private const float PanelDistance = 1.5f;

    private sealed class Step
    {
        public string Title;
        public string Body;
        public string DetectClass;   // COCO label, or null
        public int TimerSeconds;     // 0 = no timer
    }

    private sealed class AppButton
    {
        public RectTransform Rect;
        public Image Bg;
        public TextMeshProUGUI Label;
        public Action OnClick;
        public readonly Color Normal = new Color(0.18f, 0.24f, 0.32f, 1f);
        public readonly Color Hover = new Color(0.30f, 0.52f, 0.80f, 1f);
    }

    private sealed class TimerChip
    {
        public GameObject Root;
        public RectTransform Rect;
        public Image Bg;
        public TextMeshProUGUI TimeText;
        public float Remaining;
        public bool Done;
        public int LastShownSecond = -1;
    }

    private static readonly Step[] Recipe =
    {
        new Step { Title = "Broccoli & Carrot Stir-Fry",
                   Body = "A quick weeknight stir-fry. I'll walk you through it step by step — pinch Next, or pinch the panel, to move on. Keep your ingredients out where I can see them." },
        new Step { Title = "Heat the pan",
                   Body = "Put your widest pan on medium-high heat. Add a tablespoon of oil and let it shimmer — about a minute. If it smokes hard, lift it off the heat for a few seconds." },
        new Step { Title = "Show me the carrots",
                   Body = "Hold the carrots up where I can see them. I'll check them off automatically when I spot them — no need to pinch anything.",
                   DetectClass = "carrot" },
        new Step { Title = "Show me the broccoli",
                   Body = "Same trick with the broccoli. Cut it into small florets if you haven't — small pieces cook fast and get those crispy edges.",
                   DetectClass = "broccoli" },
        new Step { Title = "Stir-fry",
                   Body = "Everything into the pan: carrots first for a minute, then broccoli. Start the timer and stir every 30 seconds or so, letting the edges catch the heat.",
                   TimerSeconds = 180 },
        new Step { Title = "Season",
                   Body = "Off the heat: soy sauce, a pinch of salt, garlic if you're using it. Toss once more so everything glistens." },
        new Step { Title = "Done — nice work",
                   Body = "Plate it up. That's dinner, cooked with your hands free the whole way through." },
    };

    private Camera _cam;
    private OVRHand[] _hands = new OVRHand[0];
    private float _handScanTimer;

    // Placement (settled-pose, from the diagnostic)
    private bool _placed;
    private float _bootTime;
    private int _stableFrames;
    private Vector3 _lastHeadPos;

    // Pinch state
    private bool _pinchWas;
    private float _pinchStartTime;
    private bool _pinchFromPanel;
    private bool _summonFired;

    // Occlusion enforcement (proven pattern from the diagnostic)
    private Material _occlusionMat;
    private bool _occlusionWarned;
    private float _rescanTimer;
    private readonly List<Renderer> _handRenderers = new List<Renderer>();

    // UI
    private GameObject _panelRoot;
    private Canvas _canvas;
    private RectTransform _canvasRect;
    private TextMeshProUGUI _titleText, _counterText, _stepCounterText, _stepTitleText, _stepBodyText, _hintText;
    private GameObject _hintChip;
    private AppButton _backBtn, _nextBtn, _timerBtn;
    private readonly List<AppButton> _buttons = new List<AppButton>();
    private AppButton _hovered;
    private GameObject _reticle;
    private Material _reticleMat;
    private readonly List<TimerChip> _timers = new List<TimerChip>();
    private int _stepIndex;
    private int _lastSeenCount = -1;
    private float _detectSatisfiedFor;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (FindAnyObjectByType<KitchenGuideApp>() != null) return;
        new GameObject("KitchenGuideApp").AddComponent<KitchenGuideApp>();
        Debug.Log(Tag + " app bootstrapped");
    }

    private void Start()
    {
        _cam = Camera.main;
        if (_cam == null) _cam = FindAnyObjectByType<Camera>();
        if (_cam == null)
        {
            Debug.LogWarning(Tag + " no camera found; app idle.");
            enabled = false;
            return;
        }
        _bootTime = Time.time;
        _lastHeadPos = _cam.transform.position;

        BuildUi();
        FindHands();
        RequestBoundarySuppression();
        RefreshStepUi();
        RefreshCounterUi();

        if (OVRManager.display != null)
            OVRManager.display.RecenteredPose += OnRecenteredPose;
    }

    private void OnRecenteredPose()
    {
        // The tracking frame just rotated — re-place in front of the user.
        PlacePanel();
    }

    // Contextual Boundaryless (Boundary API): this app is always in
    // passthrough — the user sees their kitchen — so the Guardian boundary
    // is pure friction. OVRManager owns the switch; reflection is used on
    // purpose because the property's static/instance shape is described
    // inconsistently across SDK references. This resolves either shape,
    // and the delayed readback (see Update) logs the system's verdict.
    private float _boundaryCheckAt = -1f;

    private void RequestBoundarySuppression()
    {
        var mgr = FindAnyObjectByType<OVRManager>();
        if (mgr == null)
        {
            Debug.LogWarning(Tag + " no OVRManager found — boundary suppression skipped");
            return;
        }
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        var prop = typeof(OVRManager).GetProperty("shouldBoundaryVisibilityBeSuppressed", Flags);
        if (prop == null || !prop.CanWrite)
        {
            Debug.LogWarning(Tag + " shouldBoundaryVisibilityBeSuppressed not found/writable");
            return;
        }
        var setter = prop.GetSetMethod(true);
        prop.SetValue(setter != null && setter.IsStatic ? null : mgr, true);
        _boundaryCheckAt = Time.time + 3f;
        Debug.Log(Tag + " boundary suppression requested (contextual boundaryless)");
    }

    private void CheckBoundarySuppression()
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        var prop = typeof(OVRManager).GetProperty("isBoundaryVisibilitySuppressed", Flags);
        if (prop == null) return;
        var getter = prop.GetGetMethod(true);
        var mgr = FindAnyObjectByType<OVRManager>();
        object val = prop.GetValue(getter != null && getter.IsStatic ? null : mgr);
        Debug.Log(Tag + " boundary suppressed state: " + val);
    }

    // ---------------------------------------------------------------
    // Hands: tracking scan + occlusion enforcement (proven patterns)
    // ---------------------------------------------------------------

    private void FindHands()
    {
        _hands = FindObjectsByType<OVRHand>(FindObjectsInactive.Include);
        _rescanTimer = 1f; // occlusion enforcement picks them up next frame
    }

    private void LateUpdate()
    {
        EnforceHandVisuals();
    }

    private void EnforceHandVisuals()
    {
        if (_hands.Length == 0) return;
        if (_occlusionMat == null)
            _occlusionMat = Resources.Load<Material>("HandOcclusionMaterial");

        _rescanTimer += Time.deltaTime;
        if (_rescanTimer >= 0.25f)
        {
            _rescanTimer = 0f;
            _handRenderers.Clear();
            foreach (var h in _hands)
                if (h != null)
                    _handRenderers.AddRange(h.GetComponentsInChildren<Renderer>(true));
        }
        foreach (var r in _handRenderers)
        {
            if (r == null) continue;
            if (_occlusionMat != null)
            {
                if (r.sharedMaterial != _occlusionMat) r.sharedMaterial = _occlusionMat;
                if (!r.enabled) r.enabled = true;
            }
            else if (r.enabled) r.enabled = false;
        }
        if (_occlusionMat == null && !_occlusionWarned && _handRenderers.Count > 0)
        {
            _occlusionWarned = true;
            Debug.LogWarning(Tag + " HandOcclusionMaterial missing — hands hidden instead of occluding");
        }
    }

    private static float PinchStrength(OVRHand h)
    {
        return h.GetFingerPinchStrength(OVRHand.HandFinger.Index);
    }

    private bool AnyPinch()
    {
        foreach (var h in _hands)
            if (h != null && h.IsTracked && PinchStrength(h) >= PinchThreshold)
                return true;
        return OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch)
            || OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.LTouch);
    }

    // ---------------------------------------------------------------
    // UI construction (code-built world-space canvas, Meta conventions)
    // ---------------------------------------------------------------

    private static Material MakeMat(Color c)
    {
        var sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Unlit/Color");
        var m = new Material(sh);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        m.color = c;
        return m;
    }

    private static TextMeshProUGUI MakeText(Transform parent, string name,
        Vector2 anchorMin, Vector2 anchorMax, Vector2 pos, Vector2 size,
        float fontSize, TextAlignmentOptions align, Color color, FontStyles style = FontStyles.Normal)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = anchorMin; rt.anchorMax = anchorMax;
        rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = pos; rt.sizeDelta = size;
        var t = go.AddComponent<TextMeshProUGUI>();
        if (TMP_Settings.defaultFontAsset != null) t.font = TMP_Settings.defaultFontAsset;
        t.fontSize = fontSize;
        t.alignment = align;
        t.color = color;
        t.fontStyle = style;
        t.raycastTarget = false;
        return t;
    }

    private static Image MakeImage(Transform parent, string name,
        Vector2 anchorMin, Vector2 anchorMax, Vector2 pos, Vector2 size, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = anchorMin; rt.anchorMax = anchorMax;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos; rt.sizeDelta = size;
        var img = go.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    private AppButton MakeButton(Transform parent, string label, Vector2 pos, Vector2 size, Action onClick)
    {
        var bg = MakeImage(parent, "Btn_" + label, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
            pos, size, new Color(0.18f, 0.24f, 0.32f, 1f));
        var t = MakeText(bg.transform, "Label", Vector2.zero, Vector2.one,
            Vector2.zero, Vector2.zero, 46f, TextAlignmentOptions.Center,
            new Color(0.95f, 0.96f, 0.98f));
        t.text = label;
        var rt = bg.GetComponent<RectTransform>();
        rt.pivot = new Vector2(0.5f, 0f);
        var btn = new AppButton { Rect = rt, Bg = bg, Label = t, OnClick = onClick };
        _buttons.Add(btn);
        return btn;
    }

    private void BuildUi()
    {
        _panelRoot = new GameObject("KitchenGuidePanel");
        var canvasGo = new GameObject("MainCanvas");
        canvasGo.transform.SetParent(_panelRoot.transform, false);
        _canvas = canvasGo.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.WorldSpace;
        _canvasRect = canvasGo.GetComponent<RectTransform>();
        _canvasRect.sizeDelta = new Vector2(1400f, 900f);
        _canvasRect.localScale = new Vector3(0.001f, 0.001f, 0.001f);

        var top = new Vector2(0f, 1f); var topL = new Vector2(0f, 1f); var topR = new Vector2(1f, 1f);
        MakeImage(_canvasRect, "Bg", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero,
            new Color(0.07f, 0.09f, 0.12f, 0.97f));

        _titleText = MakeText(_canvasRect, "Title", topL, topR, new Vector2(50f, -30f),
            new Vector2(-360f, 72f), 54f, TextAlignmentOptions.Left,
            new Color(0.95f, 0.96f, 0.98f), FontStyles.Bold);
        _titleText.text = Recipe[0].Title;

        _stepCounterText = MakeText(_canvasRect, "StepCounter", topR, topR, new Vector2(-50f, -38f),
            new Vector2(300f, 60f), 40f, TextAlignmentOptions.Right, new Color(0.62f, 0.68f, 0.75f));
        _stepCounterText.GetComponent<RectTransform>().pivot = new Vector2(1f, 1f);

        _counterText = MakeText(_canvasRect, "Counter", topL, topR, new Vector2(50f, -118f),
            new Vector2(-100f, 56f), 38f, TextAlignmentOptions.Left, new Color(0.62f, 0.85f, 1f));

        _stepTitleText = MakeText(_canvasRect, "StepTitle", topL, topR, new Vector2(50f, -196f),
            new Vector2(-100f, 84f), 58f, TextAlignmentOptions.Left,
            new Color(1f, 1f, 1f), FontStyles.Bold);

        _stepBodyText = MakeText(_canvasRect, "StepBody", topL, topR, new Vector2(50f, -292f),
            new Vector2(-100f, 330f), 44f, TextAlignmentOptions.TopLeft,
            new Color(0.90f, 0.92f, 0.95f));

        // Detection hint chip (visible on detect steps)
        var chipImg = MakeImage(_canvasRect, "HintChip", new Vector2(0f, 0f), new Vector2(1f, 0f),
            new Vector2(0f, 158f), new Vector2(-100f, 64f), new Color(0.10f, 0.24f, 0.17f, 1f));
        _hintChip = chipImg.gameObject;
        _hintText = MakeText(chipImg.transform, "HintText", Vector2.zero, Vector2.one,
            Vector2.zero, Vector2.zero, 38f, TextAlignmentOptions.Center, new Color(0.75f, 1f, 0.85f));
        _hintText.GetComponent<RectTransform>().pivot = new Vector2(0.5f, 0.5f);

        _backBtn = MakeButton(_canvasRect, "< Back", new Vector2(-440f, 36f), new Vector2(310f, 104f), PrevStep);
        _timerBtn = MakeButton(_canvasRect, "Start timer", new Vector2(0f, 36f), new Vector2(370f, 104f), StartStepTimer);
        _nextBtn = MakeButton(_canvasRect, "Next >", new Vector2(440f, 36f), new Vector2(310f, 104f), NextStep);

        // Gaze reticle
        _reticle = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        _reticle.name = "GazeReticle";
        Destroy(_reticle.GetComponent<Collider>());
        _reticle.transform.localScale = new Vector3(0.014f, 0.014f, 0.014f);
        _reticleMat = MakeMat(new Color(1f, 1f, 1f));
        _reticle.GetComponent<Renderer>().sharedMaterial = _reticleMat;

        _panelRoot.SetActive(false); // until placed
    }

    // ---------------------------------------------------------------
    // Step engine
    // ---------------------------------------------------------------

    private void RefreshStepUi()
    {
        var s = Recipe[_stepIndex];
        _stepCounterText.text = "Step " + (_stepIndex + 1) + " of " + Recipe.Length;
        _stepTitleText.text = s.Title;
        _stepBodyText.text = s.Body;
        bool hasDetect = s.DetectClass != null;
        _hintChip.SetActive(hasDetect);
        if (hasDetect)
            _hintText.text = "Show me the " + s.DetectClass + " — I'll check it off when I see it";
        _timerBtn.Rect.gameObject.SetActive(s.TimerSeconds > 0);
        if (s.TimerSeconds > 0)
            _timerBtn.Label.text = "Start " + (s.TimerSeconds / 60) + ":" + (s.TimerSeconds % 60).ToString("00") + " timer";
        _backBtn.Rect.gameObject.SetActive(_stepIndex > 0);
        _nextBtn.Rect.gameObject.SetActive(_stepIndex < Recipe.Length - 1);
        _detectSatisfiedFor = 0f;
    }

    private void RefreshCounterUi()
    {
        if (CounterState.Seen.Count == 0)
        {
            _counterText.text = "On your counter: nothing yet";
            return;
        }
        var names = new List<string>();
        foreach (var n in CounterState.Seen)
            names.Add(n.Length > 0 ? char.ToUpper(n[0]) + n.Substring(1) : n);
        names.Sort();
        _counterText.text = "On your counter: " + string.Join(", ", names);
    }

    private void NextStep()
    {
        if (_stepIndex < Recipe.Length - 1) { _stepIndex++; RefreshStepUi(); }
    }

    private void PrevStep()
    {
        if (_stepIndex > 0) { _stepIndex--; RefreshStepUi(); }
    }

    // ---------------------------------------------------------------
    // Timers
    // ---------------------------------------------------------------

    private void StartStepTimer()
    {
        var s = Recipe[_stepIndex];
        if (s.TimerSeconds <= 0) return;
        SpawnTimer(s.Title, s.TimerSeconds);
    }

    private void SpawnTimer(string label, int seconds)
    {
        var root = new GameObject("TimerChip");
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        var rect = root.GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(560f, 300f);
        rect.localScale = new Vector3(0.001f, 0.001f, 0.001f);

        var dir = _cam.transform.forward; dir.y = 0f; dir.Normalize();
        var right = new Vector3(dir.z, 0f, -dir.x);
        float side = (_timers.Count % 2 == 0) ? -0.38f : 0.38f;
        root.transform.position = _cam.transform.position + dir * 1.15f + right * side + new Vector3(0f, -0.12f, 0f);
        root.transform.rotation = Quaternion.LookRotation(dir);

        var bg = MakeImage(rect, "ChipBg", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero,
            new Color(0.07f, 0.09f, 0.12f, 0.97f));
        var nameT = MakeText(rect, "ChipName", new Vector2(0f, 1f), new Vector2(1f, 1f),
            new Vector2(0f, -14f), new Vector2(0f, 52f), 38f, TextAlignmentOptions.Center,
            new Color(0.62f, 0.85f, 1f));
        nameT.text = label;
        var timeT = MakeText(rect, "ChipTime", Vector2.zero, Vector2.one,
            new Vector2(0f, -20f), Vector2.zero, 120f, TextAlignmentOptions.Center,
            new Color(1f, 1f, 1f), FontStyles.Bold);
        timeT.GetComponent<RectTransform>().pivot = new Vector2(0.5f, 0.5f);

        _timers.Add(new TimerChip
        {
            Root = root, Rect = rect, Bg = bg, TimeText = timeT,
            Remaining = seconds, Done = false
        });
    }

    private void UpdateTimers(float dt)
    {
        for (int i = _timers.Count - 1; i >= 0; i--)
        {
            var chip = _timers[i];
            if (!chip.Done)
            {
                chip.Remaining -= dt;
                if (chip.Remaining <= 0f)
                {
                    chip.Remaining = 0f;
                    chip.Done = true;
                }
                int shown = Mathf.CeilToInt(chip.Remaining);
                if (shown != chip.LastShownSecond)
                {
                    chip.LastShownSecond = shown;
                    chip.TimeText.text = (shown / 60) + ":" + (shown % 60).ToString("00");
                }
            }
            else
            {
                float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 5f);
                chip.Bg.color = Color.Lerp(new Color(0.10f, 0.35f, 0.18f), new Color(0.16f, 0.55f, 0.28f), pulse);
                chip.TimeText.text = "Done!";
            }
        }
    }

    // ---------------------------------------------------------------
    // Placement + main loop
    // ---------------------------------------------------------------

    private void PlacePanel()
    {
        var fwd = _cam.transform.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
        fwd.Normalize();
        _panelRoot.transform.position = _cam.transform.position + fwd * PanelDistance;
        _panelRoot.transform.rotation = Quaternion.LookRotation(fwd);
        _panelRoot.SetActive(true);
        _placed = true;
    }

    private bool RayHitsRect(RectTransform rect, out Vector2 screenPt)
    {
        screenPt = Vector2.zero;
        var ray = new Ray(_cam.transform.position, _cam.transform.forward);
        var plane = new Plane(-rect.transform.forward, rect.transform.position);
        if (!plane.Raycast(ray, out float d) || d <= 0f) return false;
        Vector3 hit = ray.GetPoint(d);
        screenPt = RectTransformUtility.WorldToScreenPoint(_cam, hit);
        return true;
    }

    private bool GazeOn(RectTransform rect)
    {
        return RayHitsRect(rect, out Vector2 sp)
            && RectTransformUtility.RectangleContainsScreenPoint(rect, sp, _cam);
    }

    private void Update()
    {
        if (_cam == null) return;
        UpdateTimers(Time.deltaTime);

        if (_boundaryCheckAt > 0f && Time.time >= _boundaryCheckAt)
        {
            _boundaryCheckAt = -1f;
            CheckBoundarySuppression();
        }

        if (_hands.Length == 0)
        {
            _handScanTimer += Time.deltaTime;
            if (_handScanTimer >= 2f) { _handScanTimer = 0f; FindHands(); }
        }

        if (!_placed)
        {
            float h = _cam.transform.position.y;
            float moved = Vector3.Distance(_cam.transform.position, _lastHeadPos);
            _lastHeadPos = _cam.transform.position;
            if (h > 0.7f && h < 2.3f && moved < 0.02f) _stableFrames++;
            else _stableFrames = 0;
            if (_stableFrames >= 30 || Time.time - _bootTime > 8f) PlacePanel();
            return;
        }

        // Counter readout follows the detection layer's state
        if (CounterState.Seen.Count != _lastSeenCount)
        {
            _lastSeenCount = CounterState.Seen.Count;
            RefreshCounterUi();
        }

        // Detection-aware step: advance shortly after the item is seen
        var step = Recipe[_stepIndex];
        if (step.DetectClass != null && CounterState.Seen.Contains(step.DetectClass))
        {
            _detectSatisfiedFor += Time.deltaTime;
            if (_detectSatisfiedFor >= 0.8f) NextStep();
        }
        else _detectSatisfiedFor = 0f;

        // --- Gaze targeting ---
        TimerChip hoveredChip = null;
        foreach (var chip in _timers)
            if (GazeOn(chip.Rect)) { hoveredChip = chip; break; }

        AppButton newHover = null;
        if (hoveredChip == null)
            foreach (var b in _buttons)
                if (b.Rect.gameObject.activeInHierarchy && GazeOn(b.Rect)) { newHover = b; break; }

        bool onPanel = hoveredChip == null && newHover == null && GazeOn(_canvasRect);

        if (newHover != _hovered)
        {
            if (_hovered != null) _hovered.Bg.color = _hovered.Normal;
            _hovered = newHover;
            if (_hovered != null) _hovered.Bg.color = _hovered.Hover;
        }

        // Reticle: on the targeted surface, else floating ahead
        if (RayHitsRect(hoveredChip != null ? hoveredChip.Rect : _canvasRect, out _))
        {
            var ray = new Ray(_cam.transform.position, _cam.transform.forward);
            var rect = hoveredChip != null ? hoveredChip.Rect : _canvasRect;
            var plane = new Plane(-rect.transform.forward, rect.transform.position);
            if (plane.Raycast(ray, out float d))
                _reticle.transform.position = ray.GetPoint(d) - rect.transform.forward * 0.012f;
        }
        else
        {
            _reticle.transform.position = _cam.transform.position + _cam.transform.forward * 2.5f;
        }
        _reticleMat.color = (_hovered != null || hoveredChip != null) ? new Color(0.45f, 1f, 0.6f) : Color.white;

        // --- Pinch ---
        bool pinch = AnyPinch();
        if (pinch && !_pinchWas)
        {
            _pinchStartTime = Time.time;
            _pinchFromPanel = _hovered != null || onPanel || hoveredChip != null;
            _summonFired = false;
            if (hoveredChip != null && hoveredChip.Done)
            {
                Destroy(hoveredChip.Root);
                _timers.Remove(hoveredChip);
            }
            else if (_hovered != null) _hovered.OnClick();
            else if (onPanel) NextStep();
        }
        if (pinch && !_pinchFromPanel && !_summonFired
            && Time.time - _pinchStartTime >= SummonSeconds)
        {
            PlacePanel();
            _summonFired = true;
        }
        _pinchWas = pinch;
    }
}
