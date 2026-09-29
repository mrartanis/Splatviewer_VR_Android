// SPDX-License-Identifier: MIT
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using GaussianSplatting.Runtime;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using UnityEngine.XR;

/// <summary>A controller-operated VR catalog for the LAN VRPhoto server.</summary>
public sealed class VRPhotoCatalog : MonoBehaviour
{
    [Serializable] sealed class Folder { public string name; public string path; }
    [Serializable] sealed class SceneItem
    {
        public string name;
        public string path;
        public string preview_url;
        public string ply_url;
        public string revision;
        public string coordinate_system;
    }
    [Serializable] sealed class LibraryPage
    {
        public string path;
        public string parent;
        public Folder[] folders;
        public SceneItem[] scenes;
        public int page;
        public int pages;
    }
    enum Mode { Browse, Address, Pair }
    enum EntryKind { EditAddress, Budget, Local, Refresh, Parent, Folder, Scene, NextScene, Approve, Cancel }
    sealed class Entry
    {
        public string label;
        public EntryKind kind;
        public string path;
        public SceneItem scene;
    }

    const string ServerPref = "vrphoto-server-origin";
    const string BudgetPref = "vrphoto-budget-index";
    const int VisibleRows = 6;
    const int GridColumns = 3;
    const int ToolbarEntries = 4;
    const int ThumbnailCacheLimit = 32;
    const int KeyColumns = 10;
    static readonly string[] BudgetNames = { "Full", "High", "Medium", "Low" };
    static readonly string[] BudgetValues = { "1", "0.65", "0.35", "0.15" };
    static readonly string[] Keys = {
        "1", "2", "3", "4", "5", "6", "7", "8", "9", "0",
        ".", ":", "a", "b", "c", "d", "e", "f", "g", "h",
        "i", "j", "k", "l", "m", "n", "o", "p", "q", "r",
        "s", "t", "u", "v", "w", "x", "y", "z", "-", "DEL",
        "OK"
    };

    public static VRPhotoCatalog Instance { get; private set; }
    public bool IsOpen { get; private set; }
    public bool IsServerSceneActive { get; private set; }

    readonly List<Entry> _entries = new List<Entry>();
    readonly List<SceneItem> _scenes = new List<SceneItem>();
    readonly List<InputDevice> _devices = new List<InputDevice>();
    readonly Dictionary<string, Texture2D> _thumbnailCache = new Dictionary<string, Texture2D>();
    readonly HashSet<string> _thumbnailLoading = new HashSet<string>();
    readonly Queue<string> _thumbnailOrder = new Queue<string>();
    readonly List<UnityWebRequest> _thumbnailRequests = new List<UnityWebRequest>();
    GameObject _panel;
    GameObject _loadingPanel;
    Text _loadingText;
    Text _title;
    Text _status;
    Text _detail;
    Text _help;
    Text[] _rowTexts;
    Image[] _rowBgs;
    Image[] _rowBorders;
    GameObject[] _rowSelections;
    Text _position;
    Sprite _roundedSprite;
    RawImage[] _rowPreviews;
    Text[] _rowGlyphs;
    Text[] _toolbarTexts;
    Image[] _toolbarBgs;
    Text[] _keyTexts;
    Image[] _keyBgs;
    UnityWebRequest _activeDownload;
    CancellationTokenSource _nativePreparation;
    Font _font;
    Mode _mode;
    LibraryPage _library;
    string _origin;
    string _addressEntry;
    string _candidateFingerprint;
    string _pendingPath = "";
    int _pendingPage = 1;
    int _selected;
    int _scroll;
    int _keySelected;
    int _budget;
    int _viewedSceneIndex = -1;
    bool _busy;
    bool _triggerReady = true;
    bool _toggleReady = true;
    bool _backReady = true;
    bool _nextReady = true;
    bool _gripHeld;
    XRNode _gripHand;
    Vector3 _gripLastPosition;
    float _navigationCooldown;
    float _nextPoseLogTime;
    float _poseLogUntil;
    int _savedMsaa;
    float _savedEyeScale;
    float _savedRenderScale;
    UniversalRenderPipelineAsset _menuPipeline;
    Quaternion _rendererBaseRotation = Quaternion.identity;
    Vector3 _rendererBaseScale = Vector3.one;
    RuntimeSplatLoader _loader;
    VRFileBrowser _localBrowser;
    VROptionsMenu _optionsMenu;
    VRRig _rig;
    GaussianSplatRenderer _suspendedRenderer;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        _loader = FindAnyObjectByType<RuntimeSplatLoader>();
        _localBrowser = FindAnyObjectByType<VRFileBrowser>();
        _optionsMenu = FindAnyObjectByType<VROptionsMenu>();
        _rig = FindAnyObjectByType<VRRig>();
        if (_loader != null && _loader.targetRenderer != null)
        {
            _rendererBaseRotation = _loader.targetRenderer.transform.localRotation;
            _rendererBaseScale = _loader.targetRenderer.transform.localScale;
        }
        _origin = PlayerPrefs.GetString(ServerPref, "");
        _budget = Mathf.Clamp(PlayerPrefs.GetInt(BudgetPref, 0), 0, BudgetValues.Length - 1);
        _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (_font == null) _font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        BuildPanel();
        Show();
    }

    IEnumerator Start()
    {
        if (string.IsNullOrEmpty(_origin))
            BeginAddressEdit();
        else
            yield return Connect();
    }

    void OnDestroy()
    {
        RestoreMenuQuality();
        if (_roundedSprite != null) { Destroy(_roundedSprite.texture); Destroy(_roundedSprite); }
        if (Instance == this) Instance = null;
        foreach (var request in _thumbnailRequests) request.Abort();
        foreach (var texture in _thumbnailCache.Values) Destroy(texture);
        _activeDownload?.Abort();
        _nativePreparation?.Cancel();
    }

    void Update()
    {
        bool toggle = XRSettings.isDeviceActive
            ? Button(XRNode.LeftHand, CommonUsages.secondaryButton)
            : Input.GetKey(KeyCode.Tab);
        bool optionsModifier = XRSettings.isDeviceActive && Grip(XRNode.RightHand) > 0.5f;
        if (toggle && _toggleReady)
        {
            _toggleReady = false;
            if (!optionsModifier && (_optionsMenu == null || !_optionsMenu.IsOpen))
            {
                if (IsOpen) Hide();
                else if (_localBrowser == null || !_localBrowser.IsOpen) Show();
            }
        }
        else if (!toggle) _toggleReady = true;

        bool back = XRSettings.isDeviceActive
            ? Button(XRNode.RightHand, CommonUsages.secondaryButton)
            : Input.GetKey(KeyCode.Backspace);
        if (back && _backReady)
        {
            _backReady = false;
            if (IsServerSceneActive && !IsOpen)
            {
                if (_busy) { _activeDownload?.Abort(); _nativePreparation?.Cancel(); }
                Show();
                ClearLoading();
            }
            else if (IsOpen && _busy) { _activeDownload?.Abort(); _nativePreparation?.Cancel(); }
            else if (IsOpen) GoBack();
        }
        else if (!back) _backReady = true;

        if (IsServerSceneActive && !IsOpen)
        {
            HandleSceneControls();
            return;
        }
        _gripHeld = false;
        if (!IsOpen || (_localBrowser != null && _localBrowser.IsOpen)) return;

        if (_mode == Mode.Address)
        {
            HandleAddressKeyboard();
            return;
        }
        if (_busy) return;

        Vector2 axis = NavigationAxis();
        if (!XRSettings.isDeviceActive)
        {
            if (Input.GetKey(KeyCode.UpArrow)) axis.y = 1;
            if (Input.GetKey(KeyCode.DownArrow)) axis.y = -1;
        }
        _navigationCooldown -= Time.unscaledDeltaTime;
        if ((Mathf.Abs(axis.y) > 0.5f || Mathf.Abs(axis.x) > 0.5f) &&
            _navigationCooldown <= 0 && _entries.Count > 0)
        {
            int next = _selected;
            if (Mathf.Abs(axis.y) < Mathf.Abs(axis.x)) next += axis.x > 0 ? 1 : -1;
            else if (_selected < ToolbarEntries)
                next = axis.y < 0 && _entries.Count > ToolbarEntries
                    ? ToolbarEntries + Mathf.Min(_selected, GridColumns - 1) : _selected;
            else if (axis.y > 0 && _selected < ToolbarEntries + GridColumns)
                next = _selected - ToolbarEntries;
            else next += axis.y < 0 ? GridColumns : -GridColumns;
            _selected = Mathf.Clamp(next, 0, _entries.Count - 1);
            if (_selected >= ToolbarEntries)
            {
                int relative = _selected - ToolbarEntries;
                if (relative < _scroll) _scroll = relative / GridColumns * GridColumns;
                if (relative >= _scroll + VisibleRows)
                    _scroll = Mathf.Max(0, relative / GridColumns * GridColumns - GridColumns);
            }
            DrawEntries();
            if (_library != null && _selected >= _entries.Count - 4 && _library.page < _library.pages)
                StartCoroutine(LoadLibrary(_library.path, _library.page + 1));
            _navigationCooldown = 0.18f;
        }
        else if (axis.sqrMagnitude < 0.06f) _navigationCooldown = 0;

        if (_busy) return;

        bool trigger = XRSettings.isDeviceActive
            ? Trigger(XRNode.LeftHand) || Trigger(XRNode.RightHand)
            : Input.GetKey(KeyCode.Return);
        if (trigger && _triggerReady)
        {
            _triggerReady = false;
            SelectEntry();
        }
        else if (!trigger) _triggerReady = true;
    }

    void HandleSceneControls()
    {
        if (_optionsMenu != null && _optionsMenu.IsOpen) return;
        bool next = XRSettings.isDeviceActive
            ? Button(XRNode.RightHand, CommonUsages.primaryButton)
            : Input.GetKey(KeyCode.N);
        if (next && _nextReady)
        {
            _nextReady = false;
            StartCoroutine(OpenNextScene());
        }
        else if (!next) _nextReady = true;

        if (_loader == null || _loader.targetRenderer == null || _rig == null || _rig.xrCamera == null) return;
        Transform content = _loader.targetRenderer.transform;
        Vector2 stick = NavigationAxis();
        if (Mathf.Abs(stick.y) > 0.2f)
            content.position += _rig.xrCamera.transform.forward * (stick.y * 1.5f * Time.deltaTime);

        XRNode hand = Grip(XRNode.RightHand) > 0.5f ? XRNode.RightHand : XRNode.LeftHand;
        Vector3 position = default;
        bool gripping = Grip(hand) > 0.5f && TryControllerPosition(hand, out position);
        if (gripping)
        {
            if (_gripHeld && _gripHand == hand)
                content.position += position - _gripLastPosition;
            _gripLastPosition = position;
            _gripHand = hand;
        }
        _gripHeld = gripping;
    }

    void LateUpdate()
    {
        Camera camera = _rig != null ? _rig.xrCamera : Camera.main;
        if (camera == null) return;
        if (_loadingPanel != null && _loadingPanel.activeSelf)
            _loadingPanel.transform.SetPositionAndRotation(camera.transform.position + camera.transform.forward * 0.95f,
                camera.transform.rotation);
        if (IsServerSceneActive && !IsOpen && Time.unscaledTime < _poseLogUntil &&
            Time.unscaledTime >= _nextPoseLogTime && _loader != null && _loader.targetRenderer != null)
        {
            _nextPoseLogTime = Time.unscaledTime + 1f;
            Transform content = _loader.targetRenderer.transform;
            Vector3 opticalAxisPoint = content.TransformPoint(Vector3.forward);
            Vector3 cameraPoint = camera.transform.InverseTransformPoint(opticalAxisPoint);
            Matrix4x4 eyeView = camera.GetStereoViewMatrix(Camera.StereoscopicEye.Left);
            Vector3 eyePoint = eyeView.MultiplyPoint(opticalAxisPoint);
            Debug.Log($"[VRPhotoPose] camera={camera.transform.position:F3} forward={camera.transform.forward:F3} " +
                $"content={content.position:F3} forward={content.forward:F3} " +
                $"axisCamera={cameraPoint:F3} axisEye={eyePoint:F3}");
        }
    }

    void SetLoading(string message)
    {
        _loadingText.text = message;
        _loadingPanel.SetActive(true);
        if (IsServerSceneActive && _loader != null && _loader.targetRenderer != null)
            _loader.targetRenderer.m_SuspendRendering = true;
        LateUpdate();
    }

    void ClearLoading()
    {
        if (_loadingPanel != null) _loadingPanel.SetActive(false);
        if (!IsOpen && _loader != null && _loader.targetRenderer != null)
            _loader.targetRenderer.m_SuspendRendering = false;
    }

    bool TryControllerPosition(XRNode hand, out Vector3 position)
    {
        _devices.Clear();
        InputDevices.GetDevicesAtXRNode(hand, _devices);
        if (_devices.Count > 0 && _devices[0].TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 trackingPosition))
        {
            Transform trackingOrigin = _rig.cameraOffset != null ? _rig.cameraOffset : _rig.transform;
            position = trackingOrigin.TransformPoint(trackingPosition);
            return true;
        }
        position = default;
        return false;
    }

    void Show()
    {
        if (IsOpen) return;
        if (IsServerSceneActive && _loader != null && _loader.targetRenderer != null)
        {
            _suspendedRenderer = _loader.targetRenderer;
            _suspendedRenderer.m_SuspendRendering = true;
        }
        IsOpen = true;
        _panel.SetActive(true);
        if (XRSettings.isDeviceActive)
        {
            _savedEyeScale = XRSettings.eyeTextureResolutionScale;
            XRSettings.eyeTextureResolutionScale = Mathf.Max(_savedEyeScale, 1.1f);
        }
        var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        if (pipeline != null)
        {
            _menuPipeline = pipeline;
            _savedMsaa = pipeline.msaaSampleCount;
            _savedRenderScale = pipeline.renderScale;
            // URP writes this to XRDisplaySubsystem every frame, overriding
            // eyeTextureResolutionScale. Give the catalog its own sharp target.
            pipeline.renderScale = Mathf.Max(_savedRenderScale, 1.25f);
            pipeline.msaaSampleCount = 4;
        }
        var camera = Camera.main;
        if (camera != null)
        {
            Vector3 forward = camera.transform.forward;
            forward.y = 0;
            if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
            forward.Normalize();
            _panel.transform.position = camera.transform.position + forward * 1.25f;
            _panel.transform.rotation = Quaternion.LookRotation(forward);
        }
        if (!XRSettings.isDeviceActive)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        if (_mode == Mode.Browse && _library != null && !_busy)
            _status.text = _scenes.Count + " photos" + (_library.pages > _library.page ? " · more load as you scroll" : "");
        DrawEntries();
    }

    void Hide()
    {
        IsOpen = false;
        _panel.SetActive(false);
        if (IsServerSceneActive && _loader != null && _loader.targetRenderer != null)
            _loader.targetRenderer.m_SuspendRendering = false;
        RestoreMenuQuality();
        if (_suspendedRenderer != null)
        {
            _suspendedRenderer.m_SuspendRendering = false;
            _suspendedRenderer = null;
        }
        if (!XRSettings.isDeviceActive)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    void RestoreMenuQuality()
    {
        if (_savedEyeScale > 0f)
        {
            XRSettings.eyeTextureResolutionScale = _savedEyeScale;
            _savedEyeScale = 0f;
        }
        if (_menuPipeline != null)
        {
            _menuPipeline.renderScale = _savedRenderScale;
            _menuPipeline.msaaSampleCount = _savedMsaa;
            _menuPipeline = null;
        }
    }

    void BeginAddressEdit()
    {
        _mode = Mode.Address;
        _addressEntry = string.IsNullOrEmpty(_origin) ? "" : _origin.Substring("https://".Length);
        _keySelected = 0;
        _status.text = "Enter the LAN address of vrphoto server";
        DrawEntries();
    }

    void HandleAddressKeyboard()
    {
        foreach (char c in Input.inputString)
        {
            if (c == '\b' && _addressEntry.Length > 0) _addressEntry = _addressEntry.Substring(0, _addressEntry.Length - 1);
            else if (c == '\n' || c == '\r') CommitAddress();
            else if (char.IsLetterOrDigit(c) || c == '.' || c == ':' || c == '-') _addressEntry += char.ToLowerInvariant(c);
        }
        Vector2 axis = NavigationAxis();
        _navigationCooldown -= Time.unscaledDeltaTime;
        if (XRSettings.isDeviceActive && _navigationCooldown <= 0)
        {
            if (Mathf.Abs(axis.x) > 0.55f)
            {
                _keySelected = Mathf.Clamp(_keySelected + (axis.x > 0 ? 1 : -1), 0, Keys.Length - 1);
                _navigationCooldown = 0.17f;
            }
            else if (Mathf.Abs(axis.y) > 0.55f)
            {
                _keySelected = Mathf.Clamp(_keySelected + (axis.y < 0 ? KeyColumns : -KeyColumns), 0, Keys.Length - 1);
                _navigationCooldown = 0.17f;
            }
        }
        bool trigger = XRSettings.isDeviceActive && (Trigger(XRNode.LeftHand) || Trigger(XRNode.RightHand));
        if (trigger && _triggerReady)
        {
            _triggerReady = false;
            string key = Keys[_keySelected];
            if (key == "OK") CommitAddress();
            else if (key == "DEL")
            {
                if (_addressEntry.Length > 0) _addressEntry = _addressEntry.Substring(0, _addressEntry.Length - 1);
            }
            else _addressEntry += key;
        }
        else if (!trigger) _triggerReady = true;
        DrawKeyboard();
    }

    void CommitAddress()
    {
        string input = _addressEntry.Trim();
        if (!input.Contains(":")) input += ":8443";
        if (!Uri.TryCreate("https://" + input, UriKind.Absolute, out Uri uri) ||
            uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host) ||
            uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            _status.text = "Use a host or IP and port, e.g. 192.168.1.20:8443";
            return;
        }
        string origin = uri.GetLeftPart(UriPartial.Authority);
        if (!string.Equals(origin, _origin, StringComparison.OrdinalIgnoreCase))
        {
            _library = null;
            _scenes.Clear();
            IsServerSceneActive = false;
            _pendingPath = "";
            _pendingPage = 1;
        }
        _origin = origin;
        PlayerPrefs.SetString(ServerPref, _origin);
        PlayerPrefs.Save();
        _mode = Mode.Browse;
        DrawEntries();
        StartCoroutine(Connect());
    }

    IEnumerator Connect()
    {
        if (string.IsNullOrEmpty(_origin)) yield break;
        _busy = true;
        _status.text = "Checking server certificate…";
        string approved = VRPhotoCertificate.Saved(_origin);
        using (var request = UnityWebRequest.Head(_origin + "/api/runtime"))
        {
            var handler = new VRPhotoCertificateHandler(approved, string.IsNullOrEmpty(approved));
            request.certificateHandler = handler;
            yield return request.SendWebRequest();
            _busy = false;
            if (!string.IsNullOrEmpty(handler.Presented) &&
                !string.Equals(handler.Presented, approved, StringComparison.OrdinalIgnoreCase))
            {
                _library = null;
                _candidateFingerprint = handler.Presented;
                _mode = Mode.Pair;
                _status.text = "Compare this SHA-256 fingerprint with the server log";
                BuildEntries();
                yield break;
            }
            if (request.result != UnityWebRequest.Result.Success)
            {
                _status.text = "Connection failed: " + request.error;
                BuildEntries();
                yield break;
            }
        }
        yield return LoadLibrary(_pendingPath, _pendingPage);
    }

    IEnumerator LoadLibrary(string path, int page)
    {
        if (string.IsNullOrEmpty(_origin) || string.IsNullOrEmpty(VRPhotoCertificate.Saved(_origin))) yield break;
        if (_busy) yield break;
        bool append = page > 1 && _library != null && string.Equals(path, _library.path, StringComparison.Ordinal);
        _busy = true;
        _status.text = append ? "Loading more photos…" : "Loading library…";
        string url = _origin + "/api/v1/library?path=" + Uri.EscapeDataString(path ?? "") + "&page=" + page;
        using (var request = UnityWebRequest.Get(url))
        {
            var handler = new VRPhotoCertificateHandler(VRPhotoCertificate.Saved(_origin));
            request.certificateHandler = handler;
            yield return request.SendWebRequest();
            _busy = false;
            if (CertificateChanged(handler)) yield break;
            if (request.result != UnityWebRequest.Result.Success)
            {
                _status.text = "Library failed: " + request.error;
                BuildEntries();
                yield break;
            }
            try
            {
                LibraryPage loaded = JsonUtility.FromJson<LibraryPage>(request.downloadHandler.text);
                if (loaded == null || loaded.folders == null || loaded.scenes == null)
                    throw new FormatException("Invalid library response");
                if (!append)
                {
                    _scenes.Clear();
                    _selected = 0;
                    _scroll = 0;
                    _viewedSceneIndex = -1;
                }
                _library = loaded;
                _scenes.AddRange(loaded.scenes);
                _pendingPath = _library.path;
                _pendingPage = _library.page;
                _status.text = _scenes.Count + " photos" + (_library.pages > _library.page ? " · more load as you scroll" : "");
                BuildEntries();
            }
            catch (Exception error)
            {
                _status.text = "Library data error: " + error.Message;
            }
        }
    }

    bool CertificateChanged(VRPhotoCertificateHandler handler)
    {
        if (string.IsNullOrEmpty(handler.Presented) ||
            string.Equals(handler.Presented, VRPhotoCertificate.Saved(_origin), StringComparison.OrdinalIgnoreCase))
            return false;
        _candidateFingerprint = handler.Presented;
        _library = null;
        _mode = Mode.Pair;
        _status.text = "Server certificate changed. Verify the new fingerprint.";
        BuildEntries();
        return true;
    }

    void BuildEntries()
    {
        _entries.Clear();
        if (_mode == Mode.Pair)
        {
            _entries.Add(new Entry { label = "Approve matching fingerprint", kind = EntryKind.Approve });
            _entries.Add(new Entry { label = "Cancel", kind = EntryKind.Cancel });
        }
        else
        {
            _entries.Add(new Entry { label = "Server  " + (string.IsNullOrEmpty(_origin) ? "set address" : new Uri(_origin).Host), kind = EntryKind.EditAddress });
            _entries.Add(new Entry { label = "Quality  " + BudgetNames[_budget], kind = EntryKind.Budget });
            _entries.Add(new Entry { label = "Local files", kind = EntryKind.Local });
            _entries.Add(new Entry { label = "Refresh", kind = EntryKind.Refresh });
            if (_library != null)
            {
                if (_library.parent != null)
                    _entries.Add(new Entry { label = "← Parent folder", kind = EntryKind.Parent, path = _library.parent });
                foreach (Folder folder in _library.folders)
                    _entries.Add(new Entry { label = folder.name, kind = EntryKind.Folder, path = folder.path });
                foreach (SceneItem scene in _scenes)
                    _entries.Add(new Entry { label = scene.name, kind = EntryKind.Scene, scene = scene });
                if (_viewedSceneIndex >= 0 &&
                    (_viewedSceneIndex + 1 < _scenes.Count || _library.page < _library.pages))
                    _entries.Add(new Entry { label = "▶  Next photo", kind = EntryKind.NextScene });
            }
        }
        _selected = Mathf.Clamp(_selected, 0, Mathf.Max(0, _entries.Count - 1));
        int lastRow = Mathf.Max(0, (_entries.Count - ToolbarEntries - 1) / GridColumns);
        _scroll = Mathf.Clamp(_scroll, 0, Mathf.Max(0, lastRow - 1) * GridColumns);
        _scroll = _scroll / GridColumns * GridColumns;
        DrawEntries();
    }

    void SelectEntry()
    {
        if (_selected < 0 || _selected >= _entries.Count) return;
        Entry entry = _entries[_selected];
        switch (entry.kind)
        {
            case EntryKind.EditAddress: BeginAddressEdit(); break;
            case EntryKind.Budget:
                _budget = (_budget + 1) % BudgetNames.Length;
                PlayerPrefs.SetInt(BudgetPref, _budget);
                PlayerPrefs.Save();
                entry.label = "Quality  " + BudgetNames[_budget];
                DrawEntries();
                break;
            case EntryKind.Local:
                Hide();
                IsServerSceneActive = false;
                _loader?.NativeSpark?.Clear();
                if (_loader != null && _loader.targetRenderer != null)
                {
                    _loader.targetRenderer.transform.localScale = _rendererBaseScale;
                    _loader.targetRenderer.transform.localRotation = _rendererBaseRotation;
                }
                _localBrowser?.ToggleBrowser();
                break;
            case EntryKind.Refresh: StartCoroutine(Connect()); break;
            case EntryKind.Parent:
            case EntryKind.Folder: StartCoroutine(LoadLibrary(entry.path, 1)); break;
            case EntryKind.Scene: StartCoroutine(DownloadScene(entry.scene)); break;
            case EntryKind.NextScene: StartCoroutine(OpenNextScene()); break;
            case EntryKind.Approve:
                VRPhotoCertificate.Save(_origin, _candidateFingerprint);
                _candidateFingerprint = null;
                _mode = Mode.Browse;
                StartCoroutine(LoadLibrary(_pendingPath, _pendingPage));
                break;
            case EntryKind.Cancel:
                _candidateFingerprint = null;
                _mode = Mode.Browse;
                _status.text = "Connection not trusted";
                BuildEntries();
                break;
        }
    }

    IEnumerator OpenNextScene()
    {
        if (_viewedSceneIndex < 0 || _busy) yield break;
        bool overlay = !IsOpen;
        if (overlay) SetLoading("Loading next photo…");
        int nextIndex = _viewedSceneIndex + 1;
        if (nextIndex >= _scenes.Count && _library != null && _library.page < _library.pages)
            yield return LoadLibrary(_library.path, _library.page + 1);
        if (nextIndex < _scenes.Count)
            yield return DownloadScene(_scenes[nextIndex]);
        else
        {
            _status.text = "Last photo in this folder";
            if (overlay)
            {
                SetLoading(_status.text);
                yield return new WaitForSecondsRealtime(1.4f);
                ClearLoading();
            }
        }
    }

    IEnumerator DownloadScene(SceneItem scene)
    {
        bool overlay = !IsOpen;
        if (overlay) SetLoading("Loading " + scene.name + "…");
        if (_loader == null)
        {
            _status.text = "No native splat loader found";
            if (overlay) ClearLoading();
            yield break;
        }
        _busy = true;
        string url = _origin + scene.ply_url + "?budget=" + BudgetValues[_budget];
        string cache = Path.Combine(Application.persistentDataPath, "VRPhotoCache");
        Directory.CreateDirectory(cache);
        string target = Path.Combine(cache, Hash(url + "|" + scene.revision) + ".ply");
        string temporary = target + ".partial";
        if (!File.Exists(target))
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            using (var request = UnityWebRequest.Get(url))
            {
                var handler = new VRPhotoCertificateHandler(VRPhotoCertificate.Saved(_origin));
                request.certificateHandler = handler;
                request.downloadHandler = new DownloadHandlerFile(temporary);
                _activeDownload = request;
                var operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    _status.text = "Downloading " + scene.name + " · " + Mathf.RoundToInt(Mathf.Clamp01(request.downloadProgress) * 100) + "%  (B to cancel)";
                    if (overlay) SetLoading(_status.text);
                    yield return null;
                }
                _activeDownload = null;
                if (CertificateChanged(handler))
                {
                    File.Delete(temporary);
                    _busy = false;
                    if (overlay) ClearLoading();
                    yield break;
                }
                if (request.result != UnityWebRequest.Result.Success)
                {
                    File.Delete(temporary);
                    _busy = false;
                    _status.text = "Download failed: " + request.error;
                    if (overlay) ClearLoading();
                    yield break;
                }
            }
            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0)
            {
                _busy = false;
                _status.text = "Downloaded PLY is empty";
                if (overlay) ClearLoading();
                yield break;
            }
            File.Move(temporary, target);
        }
        _status.text = "Preparing native splats…";
        if (overlay) SetLoading(_status.text);
        yield return null;
        bool sharp = scene.coordinate_system == "opencv-x-right-y-down-z-forward";
        bool loaded;
        if (sharp)
        {
            _nativePreparation = new CancellationTokenSource();
            var preparing = _loader.LoadSharpFileAsync(target, _nativePreparation.Token);
            while (!preparing.IsCompleted) yield return null;
            bool cancelled = _nativePreparation.IsCancellationRequested;
            _nativePreparation.Dispose();
            _nativePreparation = null;
            loaded = preparing.Status == System.Threading.Tasks.TaskStatus.RanToCompletion && preparing.Result;
            if (cancelled)
            {
                _busy = false;
                _status.text = "Loading cancelled";
                if (overlay) ClearLoading();
                yield break;
            }
        }
        else loaded = _loader.LoadFile(target);
        if (!loaded)
        {
            _busy = false;
            File.Delete(target);
            _status.text = "Could not read SHARP PLY";
            if (overlay) ClearLoading();
            yield break;
        }
        if (overlay) SetLoading("Aligning view…");
        // Resume rendering after the asynchronous preparation. Spark captures its
        // fixed scene origin from the first actual eye render after Hide().
        yield return null;
        yield return new WaitForEndOfFrame();
        if (_loader.targetRenderer != null)
        {
            Transform content = _loader.targetRenderer.transform;
            if (!sharp)
            {
                content.localScale = _rendererBaseScale;
                content.localRotation = _rendererBaseRotation;
                _rig?.ResetToSpawnPoint(_loader.targetRenderer);
            }
        }
        _viewedSceneIndex = _scenes.FindIndex(item => item.path == scene.path);
        int sceneRow = _entries.FindIndex(entry => entry.kind == EntryKind.Scene && entry.scene.path == scene.path);
        if (sceneRow >= 0)
        {
            _selected = sceneRow;
            int relative = _selected - ToolbarEntries;
            if (relative < _scroll) _scroll = relative / GridColumns * GridColumns;
            if (relative >= _scroll + VisibleRows)
                _scroll = Mathf.Max(0, relative / GridColumns * GridColumns - GridColumns);
        }
        IsServerSceneActive = true;
        _nextPoseLogTime = 0f;
        _poseLogUntil = Time.unscaledTime + 30f;
        _busy = false;
        _status.text = "Viewing " + scene.name + " · B: library · A: next · stick: depth · grip: move";
        _gripHeld = false;
        if (overlay) ClearLoading();
        Hide();
    }

    void GoBack()
    {
        if (_mode != Mode.Browse)
        {
            _mode = Mode.Browse;
            BuildEntries();
        }
        else if (_library != null && _library.parent != null)
            StartCoroutine(LoadLibrary(_library.parent, 1));
        else Hide();
    }

    void DrawEntries()
    {
        bool address = _mode == Mode.Address;
        for (int i = 0; i < _toolbarTexts.Length; i++)
        {
            bool shown = !address && i < _entries.Count;
            _toolbarBgs[i].gameObject.SetActive(shown);
            if (!shown) continue;
            bool pairing = _mode == Mode.Pair;
            Rect(_toolbarBgs[i].gameObject, 36 + i * (pairing ? 574 : 286), -158, pairing ? 554 : 270, 46);
            Rect(_toolbarTexts[i].gameObject, 10, 0, pairing ? 534 : 250, 46);
            _toolbarTexts[i].text = _entries[i].label;
            _toolbarBgs[i].color = i == _selected
                ? new Color(0.15f, 0.36f, 0.35f)
                : new Color(0.10f, 0.14f, 0.18f);
        }
        for (int i = 0; i < _rowTexts.Length; i++)
        {
            int index = ToolbarEntries + _scroll + i;
            bool shown = _mode == Mode.Browse && index < _entries.Count;
            _rowBorders[i].gameObject.SetActive(shown);
            if (!shown) continue;
            Entry entry = _entries[index];
            _rowTexts[i].text = entry.label;
            bool selected = index == _selected;
            _rowBgs[i].color = selected ? new Color(0.12f, 0.22f, 0.27f) : new Color(0.085f, 0.11f, 0.15f);
            _rowBorders[i].color = selected ? new Color(0.38f, 0.87f, 0.77f) : new Color(0.18f, 0.22f, 0.27f);
            _rowSelections[i].SetActive(selected);
            string url = entry.kind == EntryKind.Scene && !string.IsNullOrEmpty(entry.scene.preview_url)
                ? _origin + entry.scene.preview_url + (entry.scene.preview_url.Contains("?") ? "&" : "?") + "size=768" : null;
            _rowPreviews[i].gameObject.SetActive(false);
            _rowGlyphs[i].gameObject.SetActive(true);
            _rowGlyphs[i].text = entry.kind == EntryKind.Folder ? "FOLDER" :
                entry.kind == EntryKind.Parent ? "UP" : entry.kind == EntryKind.NextScene ? "NEXT" :
                url != null ? "LOADING" : "PHOTO";
            _rowPreviews[i].texture = null;
            if (url != null)
            {
                if (_thumbnailCache.TryGetValue(url, out Texture2D texture))
                {
                    _rowPreviews[i].texture = texture;
                    _rowPreviews[i].GetComponent<AspectRatioFitter>().aspectRatio =
                        (float)texture.width / Mathf.Max(1, texture.height);
                    _rowPreviews[i].gameObject.SetActive(true);
                    _rowGlyphs[i].gameObject.SetActive(false);
                }
                else if (!_thumbnailLoading.Contains(url) && !string.IsNullOrEmpty(VRPhotoCertificate.Saved(_origin)))
                    StartCoroutine(LoadThumbnail(url));
            }
        }
        for (int i = 0; i < Keys.Length; i++)
        {
            _keyTexts[i].gameObject.SetActive(address);
            _keyBgs[i].gameObject.SetActive(address);
        }
        _title.text = _mode == Mode.Pair ? "Verify server" : _mode == Mode.Address ? "Server address" :
            _library == null || string.IsNullOrEmpty(_library.path) ? "Photo library" : _library.path;
        _help.text = address ? "Stick: choose key · Trigger: type · B: back" :
            "Stick  Browse     Trigger  Open     B  Back";
        int count = Mathf.Max(0, _entries.Count - ToolbarEntries);
        _position.text = _mode == Mode.Browse && count > 0
            ? $"{_scroll + 1}–{Mathf.Min(_scroll + VisibleRows, count)} / {count}" +
              (_library != null && _library.page < _library.pages ? "+" : "") : "";
        _detail.text = _mode == Mode.Pair && !string.IsNullOrEmpty(_candidateFingerprint)
            ? "Compare with 'Server certificate SHA-256' in the server log:\n\n" + GroupFingerprint(_candidateFingerprint)
            : address ? "https://" + _addressEntry : "";
        Rect(_detail.gameObject, 36, address ? -158 : -270, 1128, address ? 70 : 450);
        _detail.gameObject.SetActive(_mode != Mode.Browse);
        if (address) DrawKeyboard();
    }

    void DrawKeyboard()
    {
        _detail.text = "https://" + _addressEntry;
        for (int i = 0; i < Keys.Length; i++)
            _keyBgs[i].color = i == _keySelected ? new Color(0.18f, 0.38f, 0.78f, 0.95f) : new Color(1, 1, 1, 0.08f);
    }

    IEnumerator LoadThumbnail(string url)
    {
        string requestedOrigin = _origin;
        _thumbnailLoading.Add(url);
        using (var request = UnityWebRequest.Get(url))
        {
            _thumbnailRequests.Add(request);
            var handler = new VRPhotoCertificateHandler(VRPhotoCertificate.Saved(_origin));
            request.certificateHandler = handler;
            yield return request.SendWebRequest();
            _thumbnailRequests.Remove(request);
            _thumbnailLoading.Remove(url);
            if (!string.Equals(requestedOrigin, _origin, StringComparison.OrdinalIgnoreCase)) yield break;
            if (CertificateChanged(handler)) yield break;
            if (request.result == UnityWebRequest.Result.Success)
            {
                // Mipmaps prevent shimmer when the panel is seen at an angle.
                var texture = new Texture2D(2, 2, TextureFormat.RGB24, true);
                if (!ImageConversion.LoadImage(texture, request.downloadHandler.data, true))
                { Destroy(texture); yield break; }
                texture.filterMode = FilterMode.Trilinear;
                texture.anisoLevel = 4;
                texture.wrapMode = TextureWrapMode.Clamp;
                _thumbnailCache[url] = texture;
                _thumbnailOrder.Enqueue(url);
                while (_thumbnailOrder.Count > ThumbnailCacheLimit)
                {
                    string evicted = _thumbnailOrder.Dequeue();
                    if (_thumbnailCache.Remove(evicted, out Texture2D oldTexture)) Destroy(oldTexture);
                }
                DrawEntries();
            }
        }
    }

    static string GroupFingerprint(string value)
    {
        var builder = new StringBuilder();
        for (int i = 0; i < value.Length; i += 16)
            builder.AppendLine(value.Substring(i, Mathf.Min(16, value.Length - i)));
        return builder.ToString();
    }

    static string Hash(string value)
    {
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "");
    }

    void BuildPanel()
    {
        _roundedSprite = CreateRoundedSprite();
        _panel = new GameObject("VRPhoto Catalog", typeof(RectTransform));
        _panel.transform.SetParent(transform, false);
        _panel.AddComponent<Canvas>().renderMode = RenderMode.WorldSpace;
        _panel.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 3f;
        _panel.GetComponent<RectTransform>().sizeDelta = new Vector2(1200, 900);
        _panel.transform.localScale = Vector3.one * 0.0015f;
        var background = Surface(_panel.transform, "Background", 0, 0, 1200, 900,
            new Color(0.035f, 0.05f, 0.07f));
        var brand = Label(background.transform, "Brand", 20, 36, -22, 700, 28);
        brand.text = "VRPHOTO  /  YOUR MOMENTS IN SPACE";
        brand.color = new Color(0.38f, 0.87f, 0.77f);
        _title = Label(background.transform, "Title", 42, 36, -56, 1128, 56);
        _title.fontStyle = FontStyle.Bold;
        _title.horizontalOverflow = HorizontalWrapMode.Wrap;
        _status = Label(background.transform, "Status", 23, 36, -112, 1128, 36);
        _status.color = new Color(0.65f, 0.73f, 0.79f);
        _toolbarTexts = new Text[ToolbarEntries];
        _toolbarBgs = new Image[ToolbarEntries];
        for (int i = 0; i < ToolbarEntries; i++)
        {
            var button = Surface(background.transform, "Toolbar " + i, 36 + i * 286, -158, 270, 46, Color.white);
            _toolbarBgs[i] = button;
            _toolbarTexts[i] = Label(button.transform, "Text", 24, 10, 0, 250, 46);
            _toolbarTexts[i].alignment = TextAnchor.MiddleCenter;
            _toolbarTexts[i].resizeTextForBestFit = true;
            _toolbarTexts[i].resizeTextMinSize = 20;
            _toolbarTexts[i].resizeTextMaxSize = 24;
        }
        _rowTexts = new Text[VisibleRows];
        _rowBgs = new Image[VisibleRows];
        _rowBorders = new Image[VisibleRows];
        _rowSelections = new GameObject[VisibleRows];
        _rowPreviews = new RawImage[VisibleRows];
        _rowGlyphs = new Text[VisibleRows];
        for (int i = 0; i < VisibleRows; i++)
        {
            var card = Surface(background.transform, "Photo card " + i,
                36 + i % GridColumns * 382, -224 - i / GridColumns * 300, 364, 282, Color.white);
            _rowBorders[i] = card;
            var body = Surface(card.transform, "Body", 3, -3, 358, 276, Color.white);
            _rowBgs[i] = body;
            var picture = Surface(body.transform, "Picture frame", 7, -7, 344, 212,
                new Color(0.022f, 0.03f, 0.04f));
            _rowPreviews[i] = Child(picture.transform, "Thumbnail").AddComponent<RawImage>();
            Rect(_rowPreviews[i].gameObject, 0, 0, 344, 212);
            _rowPreviews[i].rectTransform.pivot = new Vector2(0.5f, 0.5f);
            _rowPreviews[i].color = Color.white;
            _rowPreviews[i].raycastTarget = false;
            var aspect = _rowPreviews[i].gameObject.AddComponent<AspectRatioFitter>();
            aspect.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            _rowGlyphs[i] = Label(picture.transform, "Placeholder", 28, 0, 0, 344, 212);
            _rowGlyphs[i].alignment = TextAnchor.MiddleCenter;
            _rowGlyphs[i].color = new Color(0.46f, 0.64f, 0.67f);
            _rowTexts[i] = Label(body.transform, "Title", 29, 14, -224, 330, 44);
            _rowTexts[i].resizeTextForBestFit = true;
            _rowTexts[i].resizeTextMinSize = 23;
            _rowTexts[i].resizeTextMaxSize = 29;
            _rowTexts[i].horizontalOverflow = HorizontalWrapMode.Wrap;
            var selection = Surface(picture.transform, "Selected", 234, -10, 100, 32,
                new Color(0.38f, 0.87f, 0.77f));
            _rowSelections[i] = selection.gameObject;
            var open = Label(selection.transform, "Open", 19, 0, 0, 100, 32);
            open.text = "OPEN";
            open.fontStyle = FontStyle.Bold;
            open.color = new Color(0.02f, 0.12f, 0.12f);
            open.alignment = TextAnchor.MiddleCenter;
        }
        var divider = Child(background.transform, "Footer line");
        Rect(divider, 36, -834, 1128, 1);
        divider.AddComponent<Image>().color = new Color(0.18f, 0.23f, 0.27f);
        _help = Label(background.transform, "Help", 23, 36, -848, 860, 32);
        _help.color = new Color(0.69f, 0.77f, 0.81f);
        _position = Label(background.transform, "Position", 23, 906, -848, 258, 32);
        _position.alignment = TextAnchor.MiddleRight;
        _position.color = _help.color;
        _detail = Label(background.transform, "Details", 30, 36, -270, 1128, 450);
        _detail.alignment = TextAnchor.UpperLeft;
        _detail.horizontalOverflow = HorizontalWrapMode.Wrap;
        _keyTexts = new Text[Keys.Length];
        _keyBgs = new Image[Keys.Length];
        for (int i = 0; i < Keys.Length; i++)
        {
            var key = Surface(background.transform, "Key " + i,
                36 + i % KeyColumns * 113, -238 - i / KeyColumns * 100, 104, 88, Color.white);
            _keyBgs[i] = key;
            _keyTexts[i] = Label(key.transform, "Label", 32, 0, 0, 104, 88);
            _keyTexts[i].alignment = TextAnchor.MiddleCenter;
            _keyTexts[i].text = Keys[i];
        }
        _loadingPanel = new GameObject("VRPhoto Loading", typeof(RectTransform));
        _loadingPanel.transform.SetParent(transform, false);
        _loadingPanel.AddComponent<Canvas>().renderMode = RenderMode.WorldSpace;
        _loadingPanel.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 3f;
        _loadingPanel.GetComponent<RectTransform>().sizeDelta = new Vector2(650, 120);
        _loadingPanel.transform.localScale = Vector3.one * 0.0015f;
        var loadingBackground = Surface(_loadingPanel.transform, "Background", 0, 0, 650, 120,
            new Color(0.035f, 0.05f, 0.07f));
        _loadingText = Label(loadingBackground.transform, "Message", 28, 22, -12, 606, 96);
        _loadingText.alignment = TextAnchor.MiddleCenter;
        _loadingText.horizontalOverflow = HorizontalWrapMode.Wrap;
        _loadingPanel.SetActive(false);
        _panel.SetActive(false);
    }

    Image Surface(Transform parent, string name, float x, float y, float width, float height, Color color)
    {
        var child = Child(parent, name);
        Rect(child, x, y, width, height);
        var image = child.AddComponent<Image>();
        image.sprite = _roundedSprite;
        image.type = Image.Type.Sliced;
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    static Sprite CreateRoundedSprite()
    {
        const int size = 64;
        const float radius = 14f;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.name = "Catalog rounded corners";
        texture.filterMode = FilterMode.Bilinear;
        texture.wrapMode = TextureWrapMode.Clamp;
        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = Mathf.Max(radius - (x + 0.5f), x + 0.5f - (size - radius), 0);
            float dy = Mathf.Max(radius - (y + 0.5f), y + 0.5f - (size - radius), 0);
            byte alpha = (byte)Mathf.RoundToInt(255 * Mathf.Clamp01(radius + 0.5f - Mathf.Sqrt(dx * dx + dy * dy)));
            pixels[y * size + x] = new Color32(255, 255, 255, alpha);
        }
        texture.SetPixels32(pixels);
        texture.Apply(false, true);
        return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100, 0,
            SpriteMeshType.FullRect, new Vector4(radius, radius, radius, radius));
    }

    static GameObject Child(Transform parent, string name)
    {
        var child = new GameObject(name, typeof(RectTransform));
        child.transform.SetParent(parent, false);
        return child;
    }

    static void Rect(GameObject gameObject, float x, float y, float width, float height)
    {
        var rect = gameObject.GetComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
        rect.anchoredPosition = new Vector2(x, y);
        rect.sizeDelta = new Vector2(width, height);
    }

    Text Label(Transform parent, string name, int size, float x, float y, float width, float height)
    {
        var child = Child(parent, name);
        Rect(child, x, y, width, height);
        var label = child.AddComponent<Text>();
        label.font = _font;
        label.raycastTarget = false;
        label.supportRichText = false;
        label.fontSize = size;
        label.color = Color.white;
        label.alignment = TextAnchor.MiddleLeft;
        label.horizontalOverflow = HorizontalWrapMode.Overflow;
        label.verticalOverflow = VerticalWrapMode.Truncate;
        return label;
    }

    bool Button(XRNode node, InputFeatureUsage<bool> usage)
    {
        _devices.Clear();
        InputDevices.GetDevicesAtXRNode(node, _devices);
        return _devices.Count > 0 && _devices[0].TryGetFeatureValue(usage, out bool value) && value;
    }

    bool Trigger(XRNode node)
    {
        _devices.Clear();
        InputDevices.GetDevicesAtXRNode(node, _devices);
        return _devices.Count > 0 && _devices[0].TryGetFeatureValue(CommonUsages.trigger, out float value) && value > 0.5f;
    }

    float Grip(XRNode node)
    {
        _devices.Clear();
        InputDevices.GetDevicesAtXRNode(node, _devices);
        return _devices.Count > 0 && _devices[0].TryGetFeatureValue(CommonUsages.grip, out float value) ? value : 0f;
    }

    Vector2 NavigationAxis()
    {
        _devices.Clear();
        InputDevices.GetDevicesAtXRNode(XRNode.LeftHand, _devices);
        if (_devices.Count > 0 && _devices[0].TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 left) && left.sqrMagnitude > 0.1f)
            return left;
        _devices.Clear();
        InputDevices.GetDevicesAtXRNode(XRNode.RightHand, _devices);
        return _devices.Count > 0 && _devices[0].TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 right) ? right : Vector2.zero;
    }
}
