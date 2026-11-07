// KitchenGuideDetection.cs — on-device ingredient detection (Step B, 2026-11-07)
//
// Adapted from Meta's MultiObjectDetection sample (PassthroughCameraApiSamples;
// sample code © Meta Platforms, Oculus SDK License; the YOLO model + class list
// are MIT per that repo's README). The pipeline is the sample's: Passthrough
// Camera API frames -> Sentis (YOLOv9t, Uint8) -> C# NMS -> 3D placement via
// ViewportPointToRay + MRUK Environment Raycast (Depth API, no room scan).
//
// Deliberate deviations from the sample, per the lift plan:
//   * No sample menu/pause state machine — detection runs when the camera does.
//   * NO spatial-anchor save gate. The sample discards all results until a
//     SAVED OVRSpatialAnchor is tracked; anchor saving is unreliable for
//     sideloaded builds and would fake a "detection doesn't work" result.
//     Labels are placed from live depth each refresh instead (3 s persist).
//   * Results feed the app: kitchen COCO classes go into CounterState (the
//     panel's counter strip + detect-to-advance recipe steps read it), and
//     each currently-visible kitchen item gets a floating TMP label chip.
//   * Inference is throttled (~3 Hz): cooking doesn't need 30 Hz detection,
//     and the CPU worker shares the device with rendering.
// Self-bootstrapping; model + labels load from Resources/Models.

using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Meta.XR;
using TMPro;
using Unity.Collections;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.UI;

#if UNITY_ANDROID
using UnityEngine.Android;
#endif

public class KitchenGuideDetection : MonoBehaviour
{
    private const string Tag = "[KGDetection]";
    private const string CameraPermission = "horizonos.permission.HEADSET_CAMERA";
    private const string ScenePermission = "com.oculus.permission.USE_SCENE";
    private const float ScoreThreshold = 0.3f;   // sample prefab values,
    private const float IouThreshold = 0.4f;     // tuned by Meta for this model
    private const float MinInferenceInterval = 0.35f;
    private const float LabelPersistSeconds = 3f;

    private static readonly HashSet<string> KitchenClasses = new HashSet<string>
    {
        "banana", "apple", "orange", "broccoli", "carrot", "cup", "bowl",
        "bottle", "knife", "fork", "spoon", "microwave", "oven", "toaster",
        "sink", "refrigerator"
    };

    private PassthroughCameraAccess _cameraAccess;
    private EnvironmentRaycastManager _raycastManager;
    private Worker _worker;
    private Vector2Int _inputSize;
    private string[] _labels;
    private bool _cameraAnnounced;
    private float _lastInferenceStart = -10f;

    private readonly List<(int classId, Vector4 boundingBox)> _detections =
        new List<(int classId, Vector4 boundingBox)>();

    private sealed class LabelChip
    {
        public GameObject Root;
        public TextMeshProUGUI Text;
        public float LastSeen;
    }

    private readonly Dictionary<string, LabelChip> _chips = new Dictionary<string, LabelChip>();
    private readonly List<LabelChip> _chipPool = new List<LabelChip>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (FindAnyObjectByType<KitchenGuideDetection>() != null) return;
        new GameObject("KitchenGuideDetection").AddComponent<KitchenGuideDetection>();
        Debug.Log(Tag + " detection bootstrapped");
    }

    private void Start()
    {
        var labelsAsset = Resources.Load<TextAsset>("Models/SentisYoloClasses");
        var modelAsset = Resources.Load<ModelAsset>("Models/yolov9sentis");
        if (labelsAsset == null || modelAsset == null)
        {
            Debug.LogError(Tag + " model or labels missing from Resources/Models — detection disabled");
            enabled = false;
            return;
        }
        _labels = labelsAsset.text.Trim().Split('\n');
        for (int i = 0; i < _labels.Length; i++) _labels[i] = _labels[i].Trim();

        var model = ModelLoader.Load(modelAsset);
        var inputShape = model.inputs[0].shape;
        _inputSize = new Vector2Int(inputShape.Get(2), inputShape.Get(3));
        _worker = new Worker(model, BackendType.CPU);
        Debug.Log(Tag + " model loaded, input " + _inputSize.x + "x" + _inputSize.y + ", " + _labels.Length + " classes");

        var camGo = new GameObject("PassthroughCamera");
        camGo.transform.SetParent(transform, false);
        _cameraAccess = camGo.AddComponent<PassthroughCameraAccess>();

        var rayGo = new GameObject("EnvironmentRaycast");
        rayGo.transform.SetParent(transform, false);
        _raycastManager = rayGo.AddComponent<EnvironmentRaycastManager>();
        if (!EnvironmentRaycastManager.IsSupported)
            Debug.LogWarning(Tag + " EnvironmentRaycastManager reports NOT supported — labels will use fallback distance");

        RequestPermissions();
        StartCoroutine(InferenceLoop());
    }

    private void OnDestroy()
    {
        if (_worker == null) return;
        _worker.PeekOutput(0)?.CompleteAllPendingOperations();
        _worker.PeekOutput(1)?.CompleteAllPendingOperations();
        _worker.PeekOutput(2)?.CompleteAllPendingOperations();
        _worker.Dispose();
    }

    private void RequestPermissions()
    {
#if UNITY_ANDROID
        if (!Permission.HasUserAuthorizedPermission(CameraPermission))
            Permission.RequestUserPermission(CameraPermission);
        if (!Permission.HasUserAuthorizedPermission(ScenePermission))
            Permission.RequestUserPermission(ScenePermission);
#endif
    }

    // ---------------------------------------------------------------
    // Inference loop (sample's, re-gated and throttled)
    // ---------------------------------------------------------------

    [DllImport("OVRPlugin", CallingConvention = CallingConvention.Cdecl)]
    private static extern OVRPlugin.Result ovrp_GetNodePoseStateAtTime(
        double time, OVRPlugin.Node nodeId, out OVRPlugin.PoseStatef nodePoseState);

    private IEnumerator InferenceLoop()
    {
        while (true)
        {
            if (_cameraAccess == null || !_cameraAccess.IsPlaying)
            {
                yield return new WaitForSeconds(0.25f);
                continue;
            }
            if (!_cameraAnnounced)
            {
                _cameraAnnounced = true;
                Debug.Log(Tag + " camera playing at " + _cameraAccess.CurrentResolution);
            }
            float wait = MinInferenceInterval - (Time.time - _lastInferenceStart);
            if (wait > 0f) yield return new WaitForSeconds(wait);
            _lastInferenceStart = Time.time;
            yield return RunInference();
        }
    }

    private IEnumerator RunInference()
    {
        if (!ovrp_GetNodePoseStateAtTime(OVRPlugin.GetTimeInSeconds(), OVRPlugin.Node.Head, out _).IsSuccess())
            yield break; // head pose unreliable this frame — camera pose would be too

        var cachedCameraPose = _cameraAccess.GetCameraPose();
        Texture targetTexture = _cameraAccess.GetTexture();
        if (targetTexture == null) yield break;

        var textureTransform = new TextureTransform().SetDimensions(targetTexture.width, targetTexture.height, 3);
        using var input = new Tensor<float>(new TensorShape(1, 3, _inputSize.x, _inputSize.y));
        TextureConverter.ToTensor(targetTexture, input, textureTransform);
        _worker.Schedule(input);

        var boxesAwaiter = (_worker.PeekOutput(0) as Tensor<float>).ReadbackAndCloneAsync().GetAwaiter();
        while (!boxesAwaiter.IsCompleted) yield return null;
        using var boxes = boxesAwaiter.GetResult();
        if (boxes.shape[0] == 0) yield break;

        var classIDsAwaiter = (_worker.PeekOutput(1) as Tensor<int>).ReadbackAndCloneAsync().GetAwaiter();
        while (!classIDsAwaiter.IsCompleted) yield return null;
        using var classIDs = classIDsAwaiter.GetResult();
        if (classIDs.shape[0] == 0) yield break;

        var scoresAwaiter = (_worker.PeekOutput(2) as Tensor<float>).ReadbackAndCloneAsync().GetAwaiter();
        while (!scoresAwaiter.IsCompleted) yield return null;
        using var scores = scoresAwaiter.GetResult();
        if (scores.shape[0] == 0) yield break;

        NonMaxSuppression(_detections, boxes, classIDs, scores, IouThreshold, ScoreThreshold);
        HandleDetections(cachedCameraPose);
    }

    private static void NonMaxSuppression(List<(int classId, Vector4 boundingBox)> outDetections,
        Tensor<float> boxes, Tensor<int> classIDs, Tensor<float> scores, float iouThreshold, float scoreThreshold)
    {
        outDetections.Clear();
        var filteredIndices = new List<int>();
        NativeArray<float>.ReadOnly scoresArray = scores.AsReadOnlyNativeArray();
        for (int i = 0; i < scoresArray.Length; i++)
            if (scoresArray[i] >= scoreThreshold) filteredIndices.Add(i);
        if (filteredIndices.Count == 0) return;

        filteredIndices.Sort((a, b) => scoresArray[b].CompareTo(scoresArray[a]));

        var suppressed = new bool[filteredIndices.Count];
        for (int i = 0; i < filteredIndices.Count; i++)
        {
            if (suppressed[i]) continue;
            int idx = filteredIndices[i];
            outDetections.Add((classIDs[idx], GetBox(idx)));
            for (int j = i + 1; j < filteredIndices.Count; j++)
            {
                if (suppressed[j]) continue;
                if (CalculateIoU(GetBox(idx), GetBox(filteredIndices[j])) > iouThreshold)
                    suppressed[j] = true;
            }
        }

        Vector4 GetBox(int i) => new Vector4(boxes[i, 0], boxes[i, 1], boxes[i, 2], boxes[i, 3]);
    }

    private static float CalculateIoU(Vector4 boxA, Vector4 boxB)
    {
        float x1 = Mathf.Max(boxA.x, boxB.x);
        float y1 = Mathf.Max(boxA.y, boxB.y);
        float x2 = Mathf.Min(boxA.z, boxB.z);
        float y2 = Mathf.Min(boxA.w, boxB.w);
        float intersectionArea = Mathf.Max(0, x2 - x1) * Mathf.Max(0, y2 - y1);
        float boxAArea = (boxA.z - boxA.x) * (boxA.w - boxA.y);
        float boxBArea = (boxB.z - boxB.x) * (boxB.w - boxB.y);
        float unionArea = boxAArea + boxBArea - intersectionArea;
        return unionArea == 0 ? 0 : intersectionArea / unionArea;
    }

    // ---------------------------------------------------------------
    // Results -> CounterState + label chips
    // ---------------------------------------------------------------

    private void HandleDetections(Pose cameraPose)
    {
        foreach (var (classId, box) in _detections)
        {
            if (classId < 0 || classId >= _labels.Length) continue;
            string className = _labels[classId];
            if (!KitchenClasses.Contains(className)) continue;

            if (CounterState.Seen.Add(className))
                Debug.Log(Tag + " detected (counted): " + className);

            // Placement: box center ray -> environment depth (sample's math)
            var rect = new Rect(box.x, box.y, box.z - box.x, box.w - box.y);
            Vector2 normalizedCenter = rect.center / _inputSize;
            var ray = _cameraAccess.ViewportPointToRay(
                new Vector2(normalizedCenter.x, 1.0f - normalizedCenter.y), cameraPose);

            float distance = 1.2f; // fallback when depth has no answer
            if (EnvironmentRaycastManager.IsSupported
                && _raycastManager.Raycast(ray, out var hitInfo))
                distance = Vector3.Distance(cameraPose.position, hitInfo.point);

            var worldCenter = _cameraAccess.ViewportPointToRay(
                new Vector2(normalizedCenter.x, 1.0f - normalizedCenter.y), cameraPose).GetPoint(distance);

            var chip = GetOrCreateChip(className);
            chip.Root.transform.position = worldCenter;
            chip.LastSeen = Time.time;
        }
    }

    private LabelChip GetOrCreateChip(string className)
    {
        if (_chips.TryGetValue(className, out var existing))
        {
            existing.Root.SetActive(true);
            return existing;
        }

        LabelChip chip;
        if (_chipPool.Count > 0)
        {
            chip = _chipPool[_chipPool.Count - 1];
            _chipPool.RemoveAt(_chipPool.Count - 1);
            chip.Root.SetActive(true);
        }
        else
        {
            chip = BuildChip();
        }
        chip.Text.text = className.Length > 0
            ? char.ToUpper(className[0]) + className.Substring(1)
            : className;
        _chips[className] = chip;
        return chip;
    }

    private LabelChip BuildChip()
    {
        var root = new GameObject("DetectionLabel");
        root.transform.SetParent(transform, false);
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        var rect = root.GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(360f, 104f);
        rect.localScale = new Vector3(0.001f, 0.001f, 0.001f);

        var bgGo = new GameObject("Bg");
        bgGo.transform.SetParent(rect, false);
        var bgRt = bgGo.AddComponent<RectTransform>();
        bgRt.anchorMin = Vector2.zero; bgRt.anchorMax = Vector2.one;
        bgRt.sizeDelta = Vector2.zero;
        var bg = bgGo.AddComponent<Image>();
        bg.color = new Color(0.07f, 0.09f, 0.12f, 0.92f);
        bg.raycastTarget = false;

        var textGo = new GameObject("Text");
        textGo.transform.SetParent(rect, false);
        var textRt = textGo.AddComponent<RectTransform>();
        textRt.anchorMin = Vector2.zero; textRt.anchorMax = Vector2.one;
        textRt.sizeDelta = Vector2.zero;
        var text = textGo.AddComponent<TextMeshProUGUI>();
        if (TMP_Settings.defaultFontAsset != null) text.font = TMP_Settings.defaultFontAsset;
        text.fontSize = 46f;
        text.alignment = TextAlignmentOptions.Center;
        text.color = new Color(0.85f, 1f, 0.9f);
        text.raycastTarget = false;

        return new LabelChip { Root = root, Text = text };
    }

    private void Update()
    {
        // Billboard labels toward the user; retire stale ones to the pool.
        var cam = Camera.main;
        var toRemove = new List<string>();
        foreach (var pair in _chips)
        {
            var chip = pair.Value;
            if (Time.time - chip.LastSeen > LabelPersistSeconds)
            {
                toRemove.Add(pair.Key);
                continue;
            }
            if (cam != null)
            {
                var p = chip.Root.transform.position;
                var dir = p - cam.transform.position;
                if (dir.sqrMagnitude > 0.0001f)
                    chip.Root.transform.rotation = Quaternion.LookRotation(dir);
            }
        }
        foreach (var key in toRemove)
        {
            var chip = _chips[key];
            _chips.Remove(key);
            chip.Root.SetActive(false);
            _chipPool.Add(chip);
        }
    }
}
