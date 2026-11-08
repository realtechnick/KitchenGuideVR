// KitchenGuideApp.cs — KitchenGuide functional slice (Step A, 2026-11-07)
//
// The app foundation: the gaze-pinch diagnostic's PROVEN interaction core
// (settled-pose panel placement at 1.5 m, gaze hover, pinch select,
// long-pinch summon, recenter re-place, LateUpdate hand-occlusion
// enforcement) carrying a real cooking UI:
//   * world-space canvas panel per Meta's Quest UI conventions
//     (scale 0.001, TMP text, generous targets)
//   * SYSTEM-UI interaction model (Boss's direction, 2026-11-07): a hand
//     pointer ray (OVRHand.PointerPose) with a small reticle, pinch to
//     select. Panels MOVE by pinch-dragging the small bar beneath them
//     (full-hand grip was tried three ways and removed 2026-11-07 — the
//     Interaction SDK migration owns grabbing from here); timers move by
//     pinch-dragging their border. Head pose only decides where panels
//     SPAWN. Timer chips
//     additionally carry (unsaved) spatial anchors so they stay locked
//     over appliances.
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
using Meta.XR.MRUtilityKit;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>What the detection layer has seen on the counter. Step B's
/// DetectionManager writes here; the UI reads. Class names are COCO labels.</summary>
public static class CounterState
{
    public static readonly HashSet<string> Seen = new HashSet<string>();

    // App -> detection coordination: DetectionBoost while the current recipe
    // step is waiting on a detection; Dragging while a panel/chip is mid-drag
    // (inference pauses so drags stay smooth).
    public static bool DetectionBoost;
    public static bool Dragging;
}

public class KitchenGuideApp : MonoBehaviour
{
    private const string Tag = "[KitchenGuide]";
    private const float PinchThreshold = 0.7f;
    private const float SummonSeconds = 0.8f;
    private const float PanelDistance = 0.75f; // tablet-style panel spawns within reach (Boss, 2026-11-07)

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
        public Color Normal = new Color(0.18f, 0.24f, 0.32f, 1f);
        public readonly Color Hover = new Color(0.22f, 0.62f, 0.36f, 1f);
    }

    private sealed class TimerChip
    {
        public GameObject Root;
        public RectTransform Rect;
        public Image Bg;
        public TextMeshProUGUI TimeText;
        public DragBar Bar;
        public float Remaining;
        public bool Done;
        public int LastShownSecond = -1;
    }

    // The nav pill under panels and timers — pinch-drag from a distance,
    // complementing the border grip (kept per Boss, 2026-11-07).
    private sealed class DragBar
    {
        public RectTransform Rect;
        public Image Bg;
        public Transform Root;   // what moves when this bar is dragged
        public TimerChip Chip;   // non-null when the root is a timer chip
        public readonly Color Normal = new Color(1f, 1f, 1f, 0.35f);
        public readonly Color Hover = new Color(1f, 1f, 1f, 0.95f);
    }

    private sealed class RecipeDef
    {
        public string Name;
        public Step[] Steps;
    }

    private static readonly RecipeDef[] Recipes =
    {
        new RecipeDef { Name = "Broccoli & Carrot Stir-Fry", Steps = new Step[]
        {
            new Step { Title = "Broccoli & Carrot Stir-Fry",
                       Body = "A quick weeknight stir-fry. I'll walk you through it step by step. Keep your ingredients out where I can see them." },
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
        } },
        new RecipeDef { Name = "Simple Garlic Pasta", Steps = new Step[]
        {
            new Step { Title = "Simple Garlic Pasta",
                       Body = "Five ingredients, one pan, big flavor. I'll keep time and watch for what I need — you just cook." },
            new Step { Title = "Boil the pasta",
                       Body = "Salt a big pot of water generously and get it to a rolling boil. Drop the spaghetti and start the timer.",
                       TimerSeconds = 480 },
            new Step { Title = "Show me the cup",
                       Body = "Before you drain anything: hold up a measuring cup where I can see it. We're saving a cup of that starchy pasta water — it's the sauce's secret.",
                       DetectClass = "cup" },
            new Step { Title = "Sizzle the garlic",
                       Body = "Olive oil in a wide pan, low heat, sliced garlic in. Start the timer — you want pale gold, never brown.",
                       TimerSeconds = 120 },
            new Step { Title = "Toss and serve",
                       Body = "Pasta into the garlic oil with splashes of pasta water until it shines. Parmesan over the top. Done." },
        } },
        new RecipeDef { Name = "Caprese Toast Plate", Steps = new Step[]
        {
            new Step { Title = "Caprese Toast Plate",
                       Body = "Crunchy toast, cool tomato, creamy mozzarella. Assembly food at its best — I'll pace you." },
            new Step { Title = "Toast the bread",
                       Body = "Thick slices into the toaster or a dry hot pan. Start the timer and get them deeply golden.",
                       TimerSeconds = 180 },
            new Step { Title = "Show me the bowl",
                       Body = "Hold up your mixing bowl where I can see it — the tomatoes get salted in there to draw out their juice.",
                       DetectClass = "bowl" },
            new Step { Title = "Slice and plate",
                       Body = "Thick tomato slices, a pinch of salt, two minutes to weep. Then layer tomato and mozzarella over the toast." },
            new Step { Title = "Finish and serve",
                       Body = "Basil leaves, good olive oil, flaky salt, a crack of pepper. Eat it while the toast is still warm." },
        } },
    };

    private Step[] _activeSteps;
    private string _activeRecipeName = "";

    private Camera _cam;
    private OVRHand[] _hands = new OVRHand[0];
    private float _handScanTimer;

    // Placement (settled-pose, from the diagnostic)
    private bool _placed;
    private float _bootTime;
    private float _stableFor;
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

    // UI modes: Welcome ("I'll be your sous chef") -> recipe List -> Steps.
    private enum UiMode { Welcome, List, Steps }
    private UiMode _mode = UiMode.Welcome;
    private GameObject _welcomeGroup, _listGroup, _stepsGroup;
    private TextMeshProUGUI _titleText, _counterText, _stepCounterText, _stepTitleText, _stepBodyText, _hintText;
    private GameObject _hintChip;
    private AppButton _backBtn, _nextBtn, _timerBtn;
    private readonly List<AppButton> _buttons = new List<AppButton>();
    private AppButton _hovered;
    private readonly List<DragBar> _dragBars = new List<DragBar>();
    private DragBar _pillDrag;
    private TimerChip _dragChip;
    private float _dragDistance;
    private Vector3 _dragOffset;
    private bool _anyPinchWas;
    private OVRHand _activeHand;
    private GameObject _reticle;
    private Image _reticleImg;
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

        // NOTE (Boss's world-lock law, 2026-11-07): panels and timers are
        // digital content placed precisely in the kitchen — a headset
        // recenter must NOT move them. No RecenteredPose subscription.
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

    // The Effect Mesh block renders the room model as visible blue
    // outlines by default — not this app's look, and Boss's first load
    // had them misaligned besides. Hide the DRAWING only: the mesh object
    // stays alive so Step C can still read scene data from MRUK.
    private void SuppressEffectMeshVisuals()
    {
        foreach (var em in FindObjectsByType<EffectMesh>(FindObjectsSortMode.None))
            em.ToggleEffectMeshVisibility(false);
    }

    private void EnforceHandVisuals()
    {
        if (_hands.Length == 0) return;
        if (_occlusionMat == null)
            _occlusionMat = Resources.Load<Material>("HandOcclusionMaterial");

        _rescanTimer += Time.deltaTime;
        if (_rescanTimer >= 1.0f)
        {
            _rescanTimer = 0f;
            SuppressEffectMeshVisuals();
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

    private static Sprite _circleSprite;

    // Soft-edged filled circle for the pointer reticle (system-UI style).
    private static Sprite CircleSprite()
    {
        if (_circleSprite != null) return _circleSprite;
        const int size = 128;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var px = new Color32[size * size];
        var center = new Vector2(size / 2f, size / 2f);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float d = Vector2.Distance(new Vector2(x, y), center);
            float alpha = Mathf.Clamp01(size / 2f - d + 0.5f);
            px[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
        }
        tex.SetPixels32(px);
        tex.Apply();
        _circleSprite = Sprite.Create(tex, new Rect(0, 0, size, size),
            new Vector2(0.5f, 0.5f), 100f);
        return _circleSprite;
    }

    private static Sprite _roundedSprite;

    // Procedurally generated rounded-rectangle sprite (9-sliced): soft corners
    // on every panel and button without needing art assets — all app surfaces
    // draw through it via MakeImage.
    private static Sprite RoundedSprite()
    {
        if (_roundedSprite != null) return _roundedSprite;
        const int size = 128, radius = 30;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var px = new Color32[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            var p = new Vector2(x, y);
            var c = new Vector2(
                Mathf.Clamp(p.x, radius, size - 1 - radius),
                Mathf.Clamp(p.y, radius, size - 1 - radius));
            float alpha = Mathf.Clamp01(radius - Vector2.Distance(p, c) + 0.5f);
            px[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
        }
        tex.SetPixels32(px);
        tex.Apply();
        _roundedSprite = Sprite.Create(tex, new Rect(0, 0, size, size),
            new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect,
            new Vector4(radius, radius, radius, radius));
        return _roundedSprite;
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

    private static RectTransform MakeGroup(Transform parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        return rt;
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
        img.sprite = RoundedSprite();
        img.type = Image.Type.Sliced;
        img.raycastTarget = false;
        return img;
    }

    // Timer chips get spatial anchors so they stay locked to the world (over
    // the stove, say) through tracking relocalization and long looks away.
    // Unsaved, in-session anchors only — anchor SAVING is deliberately not
    // attempted (the save-failure loop is the sideload trap from recon).
    private static void AnchorChip(TimerChip chip)
    {
        if (chip.Root.GetComponent<OVRSpatialAnchor>() == null)
            chip.Root.AddComponent<OVRSpatialAnchor>();
    }

    private static void UnanchorChip(TimerChip chip)
    {
        var a = chip.Root.GetComponent<OVRSpatialAnchor>();
        if (a != null) Destroy(a);
    }

    // The nav pill: a slim rounded handle under a panel or chip. Point at it
    // and pinch-hold to drag its root from a distance; release to drop.
    private DragBar MakeGrabBar(Transform rootToMove, Vector3 localPos, TimerChip chip, Vector2 size)
    {
        var go = new GameObject("GrabBar");
        go.transform.SetParent(rootToMove, false);
        // Compensate for the parent's scale: the panel root is unscaled
        // (positions in meters) but a timer chip's root IS its canvas
        // (scale 0.001, positions in canvas units).
        float ps = Mathf.Max(0.00001f, rootToMove.lossyScale.x);
        go.transform.localPosition = localPos / ps;
        go.transform.localScale = Vector3.one * (0.001f / ps);
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        var rect = go.GetComponent<RectTransform>();
        rect.sizeDelta = size;
        // Simple white system-style bar: translucent at rest, glowing on hover.
        var bg = MakeImage(rect, "BarBg", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero,
            new Color(1f, 1f, 1f, 0.35f));
        var bar = new DragBar { Rect = rect, Bg = bg, Root = rootToMove, Chip = chip };
        _dragBars.Add(bar);
        return bar;
    }

    private AppButton MakeButton(Transform parent, string label, Vector2 pos, Vector2 size, Color normal, Action onClick)
    {
        var bg = MakeImage(parent, "Btn_" + label, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
            pos, size, normal);
        var t = MakeText(bg.transform, "Label", Vector2.zero, Vector2.one,
            Vector2.zero, Vector2.zero, 46f, TextAlignmentOptions.Center,
            new Color(0.95f, 0.96f, 0.98f));
        t.text = label;
        var rt = bg.GetComponent<RectTransform>();
        rt.pivot = new Vector2(0.5f, 0f);
        var btn = new AppButton { Rect = rt, Bg = bg, Label = t, Normal = normal, OnClick = onClick };
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
        _canvasRect.localScale = new Vector3(0.00034f, 0.00034f, 0.00034f); // 0.48 x 0.31 m

        var top = new Vector2(0f, 1f); var topL = new Vector2(0f, 1f); var topR = new Vector2(1f, 1f);
        // A visible frame edge: it reads as "grip me" — gripping the border
        // with a full hand is literally how the panel moves now.
        MakeImage(_canvasRect, "Border", Vector2.zero, Vector2.one, Vector2.zero, new Vector2(56f, 56f),
            new Color(0.04f, 0.22f, 0.11f, 0.9f));
        MakeImage(_canvasRect, "Bg", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero,
            new Color(0.10f, 0.24f, 0.15f, 0.60f));

        var stepsRt = MakeGroup(_canvasRect, "StepsGroup");
        var listRt = MakeGroup(_canvasRect, "ListGroup");
        var welcomeRt = MakeGroup(_canvasRect, "WelcomeGroup");
        _stepsGroup = stepsRt.gameObject;
        _listGroup = listRt.gameObject;
        _welcomeGroup = welcomeRt.gameObject;

        _titleText = MakeText(stepsRt, "Title", topL, topR, new Vector2(50f, -30f),
            new Vector2(-360f, 72f), 54f, TextAlignmentOptions.Left,
            new Color(0.95f, 0.96f, 0.98f), FontStyles.Bold);
        _titleText.text = "";

        _stepCounterText = MakeText(stepsRt, "StepCounter", topR, topR, new Vector2(-50f, -38f),
            new Vector2(300f, 60f), 40f, TextAlignmentOptions.Right, new Color(0.60f, 0.72f, 0.62f));
        _stepCounterText.GetComponent<RectTransform>().pivot = new Vector2(1f, 1f);

        _counterText = MakeText(stepsRt, "Counter", topL, topR, new Vector2(50f, -118f),
            new Vector2(-100f, 56f), 38f, TextAlignmentOptions.Left, new Color(0.65f, 0.95f, 0.72f));

        _stepTitleText = MakeText(stepsRt, "StepTitle", topL, topR, new Vector2(50f, -196f),
            new Vector2(-100f, 84f), 58f, TextAlignmentOptions.Left,
            new Color(1f, 1f, 1f), FontStyles.Bold);

        _stepBodyText = MakeText(stepsRt, "StepBody", topL, topR, new Vector2(50f, -292f),
            new Vector2(-100f, 330f), 44f, TextAlignmentOptions.TopLeft,
            new Color(0.90f, 0.95f, 0.90f));
        _stepBodyText.lineSpacing = 12f;

        // Detection hint chip (visible on detect steps)
        var chipImg = MakeImage(stepsRt, "HintChip", new Vector2(0f, 0f), new Vector2(1f, 0f),
            new Vector2(0f, 158f), new Vector2(-100f, 64f), new Color(0.10f, 0.24f, 0.17f, 1f));
        _hintChip = chipImg.gameObject;
        _hintText = MakeText(chipImg.transform, "HintText", Vector2.zero, Vector2.one,
            Vector2.zero, Vector2.zero, 38f, TextAlignmentOptions.Center, new Color(0.75f, 1f, 0.85f));
        _hintText.GetComponent<RectTransform>().pivot = new Vector2(0.5f, 0.5f);

        _backBtn = MakeButton(stepsRt, "< Back", new Vector2(-440f, 36f), new Vector2(310f, 104f), new Color(0.07f, 0.15f, 0.10f, 1f), PrevStep);
        _timerBtn = MakeButton(stepsRt, "Start timer", new Vector2(0f, 36f), new Vector2(370f, 104f), new Color(0.10f, 0.28f, 0.15f, 1f), StartStepTimer);
        _nextBtn = MakeButton(stepsRt, "Next >", new Vector2(440f, 36f), new Vector2(310f, 104f), new Color(0.12f, 0.48f, 0.26f, 1f), NextStep);

        // Nav pill under the panel — the "from a distance" drag option that
        // complements border gripping (kept per Boss, 2026-11-07).
        MakeGrabBar(_panelRoot.transform, new Vector3(0f, -0.19f, 0f), null, new Vector2(90f, 9f));

        // --- Welcome group: the sous-chef greeting + Let's Cook ---
        var sousT = MakeText(welcomeRt, "SousChef", topL, topR, new Vector2(60f, -250f),
            new Vector2(-120f, 140f), 76f, TextAlignmentOptions.Center,
            new Color(0.95f, 0.98f, 0.95f), FontStyles.Italic);
        sousT.text = "I'll be your sous chef";
        sousT.characterSpacing = 2f;
        var sousSub = MakeText(welcomeRt, "SousSub", topL, topR, new Vector2(60f, -400f),
            new Vector2(-120f, 60f), 36f, TextAlignmentOptions.Center, new Color(0.68f, 0.82f, 0.70f));
        sousSub.text = "Recipes, timers, and an extra pair of eyes — right in your kitchen.";
        MakeButton(welcomeRt, "Let's Cook", new Vector2(0f, 200f), new Vector2(480f, 120f),
            new Color(0.12f, 0.48f, 0.26f, 1f), ShowRecipeList);

        // --- Recipe list group ---
        var listTitle = MakeText(listRt, "ListTitle", topL, topR, new Vector2(50f, -44f),
            new Vector2(-100f, 76f), 52f, TextAlignmentOptions.Left,
            new Color(0.95f, 0.98f, 0.95f), FontStyles.Bold);
        listTitle.text = "What are we cooking?";
        for (int i = 0; i < Recipes.Length; i++)
        {
            int idx = i;
            MakeButton(listRt, Recipes[i].Name, new Vector2(0f, 520f - i * 148f), new Vector2(780f, 116f),
                new Color(0.10f, 0.30f, 0.17f, 1f), () => StartRecipe(idx));
        }
        MakeButton(listRt, "< Back", new Vector2(-440f, 36f), new Vector2(310f, 104f),
            new Color(0.07f, 0.15f, 0.10f, 1f), ShowWelcome);

        _listGroup.SetActive(false);
        _stepsGroup.SetActive(false);

        // Pointer reticle: a small semi-transparent circle, system-UI style —
        // drawn with the UI shader (never stripped) on its own tiny canvas.
        var reticleCanvasGo = new GameObject("PointerReticle");
        var reticleCanvas = reticleCanvasGo.AddComponent<Canvas>();
        reticleCanvas.renderMode = RenderMode.WorldSpace;
        var reticleRect = reticleCanvasGo.GetComponent<RectTransform>();
        reticleRect.sizeDelta = new Vector2(120f, 120f);
        reticleRect.localScale = new Vector3(0.001f, 0.001f, 0.001f);
        var dotGo = new GameObject("Dot");
        dotGo.transform.SetParent(reticleRect, false);
        var dotRt = dotGo.AddComponent<RectTransform>();
        dotRt.sizeDelta = new Vector2(15f, 15f);
        _reticleImg = dotGo.AddComponent<Image>();
        _reticleImg.sprite = CircleSprite();
        _reticleImg.color = new Color(1f, 1f, 1f, 0.8f);
        _reticleImg.raycastTarget = false;
        _reticle = reticleCanvasGo;

        _panelRoot.SetActive(false); // until placed
    }

    // ---------------------------------------------------------------
    // Step engine
    // ---------------------------------------------------------------

    private void RefreshStepUi()
    {
        if (_activeSteps == null) return;
        var s = _activeSteps[_stepIndex];
        _stepCounterText.text = "Step " + (_stepIndex + 1) + " of " + _activeSteps.Length;
        _stepTitleText.text = s.Title;
        _stepBodyText.text = s.Body;
        bool hasDetect = s.DetectClass != null;
        CounterState.DetectionBoost = hasDetect;
        _hintChip.SetActive(hasDetect);
        if (hasDetect)
            _hintText.text = "Show me the " + s.DetectClass + " — I'll check it off when I see it";
        _timerBtn.Rect.gameObject.SetActive(s.TimerSeconds > 0);
        if (s.TimerSeconds > 0)
            _timerBtn.Label.text = "Start " + (s.TimerSeconds / 60) + ":" + (s.TimerSeconds % 60).ToString("00") + " timer";
        _backBtn.Rect.gameObject.SetActive(_stepIndex > 0);
        _nextBtn.Rect.gameObject.SetActive(_stepIndex < _activeSteps.Length - 1);
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
        if (_stepIndex < _activeSteps.Length - 1) { _stepIndex++; RefreshStepUi(); }
    }

    private void PrevStep()
    {
        if (_stepIndex > 0) { _stepIndex--; RefreshStepUi(); }
        else ShowRecipeList(); // Back from a recipe's first step returns to the list
    }

    private void ShowWelcome()
    {
        _mode = UiMode.Welcome;
        _welcomeGroup.SetActive(true);
        _listGroup.SetActive(false);
        _stepsGroup.SetActive(false);
    }

    private void ShowRecipeList()
    {
        _mode = UiMode.List;
        CounterState.DetectionBoost = false;
        _welcomeGroup.SetActive(false);
        _listGroup.SetActive(true);
        _stepsGroup.SetActive(false);
    }

    private void StartRecipe(int index)
    {
        _activeSteps = Recipes[index].Steps;
        _activeRecipeName = Recipes[index].Name;
        _stepIndex = 0;
        _mode = UiMode.Steps;
        _welcomeGroup.SetActive(false);
        _listGroup.SetActive(false);
        _stepsGroup.SetActive(true);
        _titleText.text = _activeRecipeName;
        RefreshStepUi();
    }

    // ---------------------------------------------------------------
    // Timers
    // ---------------------------------------------------------------

    private void StartStepTimer()
    {
        var s = _activeSteps[_stepIndex];
        if (s.TimerSeconds <= 0) return;
        SpawnTimer(s.Title, s.TimerSeconds);
    }

    private void SpawnTimer(string label, int seconds)
    {
        var root = new GameObject("TimerChip");
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        var rect = root.GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(300f, 168f);
        rect.localScale = new Vector3(0.001f, 0.001f, 0.001f);

        var dir = _cam.transform.forward; dir.y = 0f; dir.Normalize();
        var right = new Vector3(dir.z, 0f, -dir.x);
        float side = (_timers.Count % 2 == 0) ? -0.26f : 0.26f;
        root.transform.position = _cam.transform.position + dir * 0.9f + right * side + new Vector3(0f, -0.10f, 0f);

        MakeImage(rect, "ChipBorder", Vector2.zero, Vector2.one, Vector2.zero, new Vector2(36f, 36f),
            new Color(0.04f, 0.22f, 0.11f, 0.75f));
        var bg = MakeImage(rect, "ChipBg", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero,
            new Color(0.10f, 0.24f, 0.15f, 0.66f));
        var nameT = MakeText(rect, "ChipName", new Vector2(0f, 1f), new Vector2(1f, 1f),
            new Vector2(0f, -10f), new Vector2(0f, 42f), 30f, TextAlignmentOptions.Center,
            new Color(0.65f, 0.95f, 0.72f));
        nameT.text = label;
        // The countdown text lives on its OWN child canvas: it mutates once
        // per second, and a text change rebuilds its whole canvas — isolating
        // it keeps that rebuild to one small element instead of hitching
        // every surface (the 1-second stutter Boss caught, 2026-11-07).
        var timeCanvasGo = new GameObject("TimeCanvas");
        timeCanvasGo.transform.SetParent(rect, false);
        var timeCanvas = timeCanvasGo.AddComponent<Canvas>();
        timeCanvas.renderMode = RenderMode.WorldSpace;
        var timeRect = timeCanvasGo.GetComponent<RectTransform>();
        timeRect.sizeDelta = new Vector2(300f, 168f);
        timeRect.localScale = Vector3.one; // chip root already carries the 0.001
        var timeT = MakeText(timeRect, "ChipTime", Vector2.zero, Vector2.one,
            new Vector2(0f, -12f), Vector2.zero, 68f, TextAlignmentOptions.Center,
            new Color(1f, 1f, 1f), FontStyles.Bold);
        timeT.GetComponent<RectTransform>().pivot = new Vector2(0.5f, 0.5f);

        var chip = new TimerChip
        {
            Root = root, Rect = rect, Bg = bg, TimeText = timeT,
            Remaining = seconds, Done = false
        };
        chip.Bar = MakeGrabBar(root.transform, new Vector3(0f, -0.125f, 0f), chip, new Vector2(140f, 10f));
        _timers.Add(chip);
        FaceChipToUser(chip);
        AnchorChip(chip);
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

    // Hand pointer (Boss's direction, 2026-11-07): the system-UI model — a ray
    // cast from the pointing hand (OVRHand.PointerPose), a small reticle where
    // it lands, pinch to select. Movement is separate: grip the panel's
    // border, pinch-drag a timer's border. Head-gaze pointing is retired;
    // head pose now only decides where panels spawn.
    private bool HandPinching(OVRHand h)
    {
        return h != null && h.IsTracked && PinchStrength(h) >= PinchThreshold;
    }

    private bool TryGetPointerRay(out Ray ray)
    {
        ray = default;
        if (_activeHand == null || !_activeHand.IsTracked)
        {
            _activeHand = null;
            foreach (var h in _hands)
                if (h != null && h.IsTracked) { _activeHand = h; break; }
        }
        // A pinch from the other hand hands the pointer over to it.
        foreach (var h in _hands)
            if (h != null && h != _activeHand && HandPinching(h))
                _activeHand = h;
        if (_activeHand == null) return false;
        var pose = _activeHand.PointerPose;
        ray = new Ray(pose.position, pose.rotation * Vector3.forward);
        return true;
    }

    // Pure 3D hit-testing against the pointer ray: intersect the rect's plane,
    // then test in the rect's LOCAL space. (Screen-point round-trips are
    // unreliable against a stereo XR camera — that was the dead-buttons bug.)
    private bool PointerHitPoint(Ray ray, RectTransform rect, out Vector3 point)
    {
        point = Vector3.zero;
        var plane = new Plane(-rect.transform.forward, rect.transform.position);
        if (!plane.Raycast(ray, out float d) || d <= 0f) return false;
        point = ray.GetPoint(d);
        return true;
    }

    private bool PointerOn(Ray ray, RectTransform rect)
    {
        if (!PointerHitPoint(ray, rect, out Vector3 hit)) return false;
        Vector3 local = rect.InverseTransformPoint(hit);
        return rect.rect.Contains(new Vector2(local.x, local.y));
    }

    private void FaceChipToUser(TimerChip chip)
    {
        var dir = chip.Root.transform.position - _cam.transform.position;
        if (dir.sqrMagnitude > 0.001f)
            chip.Root.transform.rotation = Quaternion.LookRotation(dir.normalized);
    }

    private bool PointerOnBar(Ray ray, DragBar bar)
    {
        if (!PointerHitPoint(ray, bar.Rect, out Vector3 hit)) return false;
        Vector3 local = bar.Rect.InverseTransformPoint(hit);
        Rect r = bar.Rect.rect;
        return local.x >= r.xMin - 28f && local.x <= r.xMax + 28f
            && local.y >= r.yMin - 80f && local.y <= r.yMax + 16f;
    }

    private bool PointerOnChipBorder(Ray ray, TimerChip chip)
    {
        if (!PointerHitPoint(ray, chip.Rect, out Vector3 hit)) return false;
        Vector3 local = chip.Rect.InverseTransformPoint(hit);
        return InBand(chip.Rect.rect, new Vector2(local.x, local.y), 30f, 55f);
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
            if (h > 0.7f && h < 2.3f && moved < 0.02f) _stableFor += Time.deltaTime;
            else _stableFor = 0f;
            if (_stableFor >= 0.6f || Time.time - _bootTime > 8f) PlacePanel();
            return;
        }

        // Counter readout follows the detection layer's state
        if (CounterState.Seen.Count != _lastSeenCount)
        {
            _lastSeenCount = CounterState.Seen.Count;
            RefreshCounterUi();
        }

        // Detection-aware step: advance shortly after the item is seen
        if (_mode == UiMode.Steps && _activeSteps != null)
        {
            var step = _activeSteps[_stepIndex];
            if (step.DetectClass != null && CounterState.Seen.Contains(step.DetectClass))
            {
                _detectSatisfiedFor += Time.deltaTime;
                if (_detectSatisfiedFor >= 0.8f) NextStep();
            }
            else _detectSatisfiedFor = 0f;
        }

        // --- Interaction: pointer pinches select; a full-hand GRIP on the
        // panel's border moves it like a paperboard; timers pinch-drag by
        // their border ---
        bool hasRay = TryGetPointerRay(out Ray pointerRay);
        bool pointerPinch = hasRay && HandPinching(_activeHand);

        // Timer drag in progress: follows the pointer ray at grab range.
        if (_dragChip != null)
        {
            if (pointerPinch)
            {
                Vector3 target = pointerRay.GetPoint(_dragDistance) + _dragOffset;
                float k = 1f - Mathf.Exp(-18f * Time.deltaTime);
                _dragChip.Root.transform.position = Vector3.Lerp(_dragChip.Root.transform.position, target, k);
            }
            else
            {
                FaceChipToUser(_dragChip);
                AnchorChip(_dragChip);
                _dragChip = null;
            }
        }

        // Nav-pill drag in progress (panel or timer, moved from a distance).
        if (_pillDrag != null)
        {
            if (pointerPinch)
            {
                Vector3 target = pointerRay.GetPoint(_dragDistance) + _dragOffset;
                float k = 1f - Mathf.Exp(-18f * Time.deltaTime);
                _pillDrag.Root.position = Vector3.Lerp(_pillDrag.Root.position, target, k);
                var away = _pillDrag.Root.position - _cam.transform.position;
                away.y = 0f;
                if (away.sqrMagnitude > 0.001f)
                    _pillDrag.Root.rotation = Quaternion.Slerp(_pillDrag.Root.rotation,
                        Quaternion.LookRotation(away.normalized), k);
            }
            else
            {
                if (_pillDrag.Chip != null)
                {
                    FaceChipToUser(_pillDrag.Chip);
                    AnchorChip(_pillDrag.Chip);
                }
                _pillDrag.Bg.color = _pillDrag.Normal;
                _pillDrag = null;
            }
        }

        CounterState.Dragging = _dragChip != null || _pillDrag != null;

        DragBar hoveredBar = null;
        TimerChip hoveredChip = null;
        TimerChip borderChip = null;
        AppButton newHover = null;
        bool onPanel = false;
        if (hasRay && _dragChip == null && _pillDrag == null)
        {
            foreach (var bar in _dragBars)
                if (bar.Rect.gameObject.activeInHierarchy && PointerOnBar(pointerRay, bar)) { hoveredBar = bar; break; }
            if (hoveredBar == null)
            {
            foreach (var chip in _timers)
            {
                if (PointerOnChipBorder(pointerRay, chip)) { borderChip = chip; break; }
                if (hoveredChip == null && PointerOn(pointerRay, chip.Rect)) hoveredChip = chip;
            }
            if (borderChip == null && hoveredChip == null)
            {
                foreach (var b in _buttons)
                    if (b.Rect.gameObject.activeInHierarchy && PointerOn(pointerRay, b.Rect)) { newHover = b; break; }
                if (newHover == null) onPanel = PointerOn(pointerRay, _canvasRect);
            }
            }
        }

        if (newHover != _hovered)
        {
            if (_hovered != null) _hovered.Bg.color = _hovered.Normal;
            _hovered = newHover;
            if (_hovered != null) _hovered.Bg.color = _hovered.Hover;
        }
        foreach (var bar in _dragBars)
            if (bar != _pillDrag)
                bar.Bg.color = bar == hoveredBar ? bar.Hover : bar.Normal;

        // Reticle: the pointer's landing spot (system-UI style), else mid-ray
        var reticleTarget = hoveredBar != null ? hoveredBar.Rect
            : borderChip != null ? borderChip.Rect
            : hoveredChip != null ? hoveredChip.Rect : _canvasRect;
        if (hasRay && PointerHitPoint(pointerRay, reticleTarget, out Vector3 pHit))
            _reticle.transform.position = pHit - reticleTarget.transform.forward * 0.012f;
        else if (hasRay)
            _reticle.transform.position = pointerRay.GetPoint(2.5f);
        else
            _reticle.transform.position = _cam.transform.position + _cam.transform.forward * 2.5f;
        _reticle.transform.rotation = Quaternion.LookRotation(
            _reticle.transform.position - _cam.transform.position);
        _reticleImg.color = (_hovered != null || hoveredChip != null || borderChip != null || hoveredBar != null || _dragChip != null || _pillDrag != null)
            ? new Color(0.45f, 1f, 0.6f, 0.9f) : new Color(1f, 1f, 1f, 0.8f);

        // --- Pinch: pointer hand selects / drags chips; any hand summons ---
        if (pointerPinch && !_pinchWas)
        {
            if (hoveredBar != null && PointerHitPoint(pointerRay, hoveredBar.Rect, out Vector3 barHit))
            {
                _pillDrag = hoveredBar;
                _pillDrag.Bg.color = _pillDrag.Hover;
                _dragDistance = Vector3.Distance(pointerRay.origin, barHit);
                _dragOffset = _pillDrag.Root.position - barHit;
                if (_pillDrag.Chip != null) UnanchorChip(_pillDrag.Chip);
            }
            else if (borderChip != null && PointerHitPoint(pointerRay, borderChip.Rect, out Vector3 grabHit))
            {
                _dragChip = borderChip;
                _dragDistance = Vector3.Distance(pointerRay.origin, grabHit);
                _dragOffset = _dragChip.Root.transform.position - grabHit;
                UnanchorChip(_dragChip);
            }
            else if (hoveredChip != null && hoveredChip.Done)
            {
                if (hoveredChip.Bar != null) _dragBars.Remove(hoveredChip.Bar);
                Destroy(hoveredChip.Root);
                _timers.Remove(hoveredChip);
            }
            else if (_hovered != null) _hovered.OnClick();
            // Pinching the panel BODY does nothing (Boss): only buttons act.
        }
        _pinchWas = pointerPinch;

        bool anyPinch = AnyPinch();
        if (anyPinch && !_anyPinchWas)
        {
            _pinchStartTime = Time.time;
            _pinchFromPanel = _hovered != null || onPanel || hoveredChip != null
                || borderChip != null || hoveredBar != null || _dragChip != null || _pillDrag != null;
            _summonFired = false;
        }
        if (anyPinch && !_pinchFromPanel && !_summonFired
            && Time.time - _pinchStartTime >= SummonSeconds)
        {
            PlacePanel();
            _summonFired = true;
        }
        _anyPinchWas = anyPinch;
    }
}
