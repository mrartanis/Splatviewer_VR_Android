// SPDX-License-Identifier: MIT
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
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
    enum EntryKind { EditAddress, Budget, Local, Refresh, Parent, Folder, Scene, Previous, Next, Approve, Cancel }
    sealed class Entry
    {
        public string label;
        public EntryKind kind;
        public string path;
        public SceneItem scene;
    }

    const string ServerPref = "vrphoto-server-origin";
    const string BudgetPref = "vrphoto-budget-index";
    const int VisibleRows = 11;
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

    readonly List<Entry> _entries = new List<Entry>();
    readonly List<InputDevice> _devices = new List<InputDevice>();
    GameObject _panel;
    Text _title;
    Text _status;
    Text _detail;
    Text _help;
    Text[] _rowTexts;
    Image[] _rowBgs;
    Text[] _keyTexts;
    Image[] _keyBgs;
    RawImage _preview;
    Texture2D _previewTexture;
    UnityWebRequest _previewRequest;
    UnityWebRequest _activeDownload;
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
    bool _busy;
    bool _triggerReady = true;
    bool _toggleReady = true;
    bool _backReady = true;
    float _navigationCooldown;
    string _previewUrl;
    Quaternion _rendererBaseRotation = Quaternion.identity;
    RuntimeSplatLoader _loader;
    VRFileBrowser _localBrowser;
    VROptionsMenu _optionsMenu;
    VRRig _rig;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        _loader = FindAnyObjectByType<RuntimeSplatLoader>();
        _localBrowser = FindAnyObjectByType<VRFileBrowser>();
        _optionsMenu = FindAnyObjectByType<VROptionsMenu>();
        _rig = FindAnyObjectByType<VRRig>();
        if (_loader != null && _loader.targetRenderer != null)
            _rendererBaseRotation = _loader.targetRenderer.transform.localRotation;
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
        if (Instance == this) Instance = null;
        _previewRequest?.Abort();
        _activeDownload?.Abort();
        if (_previewTexture != null) Destroy(_previewTexture);
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

        if (!IsOpen || (_localBrowser != null && _localBrowser.IsOpen)) return;
        bool back = XRSettings.isDeviceActive
            ? Button(XRNode.RightHand, CommonUsages.secondaryButton)
            : Input.GetKey(KeyCode.Backspace);
        if (back && _backReady)
        {
            _backReady = false;
            if (_busy) _activeDownload?.Abort();
            else GoBack();
        }
        else if (!back) _backReady = true;

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
        if (Mathf.Abs(axis.y) > 0.5f && _navigationCooldown <= 0 && _entries.Count > 0)
        {
            _selected = Mathf.Clamp(_selected + (axis.y < 0 ? 1 : -1), 0, _entries.Count - 1);
            _scroll = Mathf.Clamp(_selected - VisibleRows + 1, 0, Mathf.Max(0, _entries.Count - VisibleRows));
            DrawEntries();
            _navigationCooldown = 0.18f;
        }
        else if (Mathf.Abs(axis.y) < 0.25f) _navigationCooldown = 0;

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

    void Show()
    {
        IsOpen = true;
        _panel.SetActive(true);
        var camera = Camera.main;
        if (camera != null)
        {
            Vector3 forward = camera.transform.forward;
            forward.y = 0;
            if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
            forward.Normalize();
            _panel.transform.position = camera.transform.position + forward * 1.35f;
            _panel.transform.rotation = Quaternion.LookRotation(forward);
        }
        if (!XRSettings.isDeviceActive)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    void Hide()
    {
        IsOpen = false;
        _panel.SetActive(false);
        if (!XRSettings.isDeviceActive)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
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
        _busy = true;
        _status.text = "Loading library…";
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
                _library = JsonUtility.FromJson<LibraryPage>(request.downloadHandler.text);
                if (_library == null || _library.folders == null || _library.scenes == null)
                    throw new FormatException("Invalid library response");
                _pendingPath = _library.path;
                _pendingPage = _library.page;
                _status.text = _library.scenes.Length + " scenes on this page";
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
            _entries.Add(new Entry { label = "Server: " + (string.IsNullOrEmpty(_origin) ? "set address" : _origin), kind = EntryKind.EditAddress });
            _entries.Add(new Entry { label = "Quality: " + BudgetNames[_budget], kind = EntryKind.Budget });
            _entries.Add(new Entry { label = "Local files", kind = EntryKind.Local });
            _entries.Add(new Entry { label = "Refresh", kind = EntryKind.Refresh });
            if (_library != null)
            {
                if (_library.parent != null)
                    _entries.Add(new Entry { label = "← Parent folder", kind = EntryKind.Parent, path = _library.parent });
                foreach (Folder folder in _library.folders)
                    _entries.Add(new Entry { label = "▣  " + folder.name, kind = EntryKind.Folder, path = folder.path });
                foreach (SceneItem scene in _library.scenes)
                    _entries.Add(new Entry { label = "▸  " + scene.name, kind = EntryKind.Scene, scene = scene });
                if (_library.page > 1)
                    _entries.Add(new Entry { label = "← Previous page", kind = EntryKind.Previous });
                if (_library.page < _library.pages)
                    _entries.Add(new Entry { label = "Next page →", kind = EntryKind.Next });
            }
        }
        _selected = 0;
        _scroll = 0;
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
                entry.label = "Quality: " + BudgetNames[_budget];
                DrawEntries();
                break;
            case EntryKind.Local:
                Hide();
                _localBrowser?.ToggleBrowser();
                break;
            case EntryKind.Refresh: StartCoroutine(Connect()); break;
            case EntryKind.Parent:
            case EntryKind.Folder: StartCoroutine(LoadLibrary(entry.path, 1)); break;
            case EntryKind.Previous: StartCoroutine(LoadLibrary(_library.path, _library.page - 1)); break;
            case EntryKind.Next: StartCoroutine(LoadLibrary(_library.path, _library.page + 1)); break;
            case EntryKind.Scene: StartCoroutine(DownloadScene(entry.scene)); break;
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

    IEnumerator DownloadScene(SceneItem scene)
    {
        if (_loader == null)
        {
            _status.text = "No native splat loader found";
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
                    yield return null;
                }
                _activeDownload = null;
                if (CertificateChanged(handler))
                {
                    File.Delete(temporary);
                    _busy = false;
                    yield break;
                }
                if (request.result != UnityWebRequest.Result.Success)
                {
                    File.Delete(temporary);
                    _busy = false;
                    _status.text = "Download failed: " + request.error;
                    yield break;
                }
            }
            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0)
            {
                _busy = false;
                _status.text = "Downloaded PLY is empty";
                yield break;
            }
            File.Move(temporary, target);
        }
        _status.text = "Preparing native splats…";
        yield return null;
        bool loaded = _loader.LoadFile(target);
        _busy = false;
        if (!loaded)
        {
            File.Delete(target);
            _status.text = "Could not read SHARP PLY";
            yield break;
        }
        bool sharp = scene.coordinate_system == "opencv-x-right-y-down-z-forward";
        if (_loader.targetRenderer != null)
            _loader.targetRenderer.transform.localRotation = _rendererBaseRotation;
        if (sharp) _rig?.ResetToSharpCaptureView();
        else _rig?.ResetToSpawnPoint(_loader.targetRenderer);
        _status.text = "Viewing " + scene.name;
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
        for (int i = 0; i < _rowTexts.Length; i++)
        {
            int index = _scroll + i;
            bool shown = !address && index < _entries.Count;
            _rowTexts[i].gameObject.SetActive(shown);
            _rowBgs[i].gameObject.SetActive(shown);
            if (shown)
            {
                _rowTexts[i].text = _entries[index].label;
                _rowBgs[i].color = index == _selected ? new Color(0.18f, 0.38f, 0.78f, 0.9f) : new Color(1, 1, 1, i % 2 == 0 ? 0.03f : 0.07f);
            }
        }
        for (int i = 0; i < Keys.Length; i++)
        {
            _keyTexts[i].gameObject.SetActive(address);
            _keyBgs[i].gameObject.SetActive(address);
        }
        _title.text = _mode == Mode.Pair ? "Verify VRPhoto server" : _mode == Mode.Address ? "Server address" :
            "VRPhoto · " + (_library == null ? "Library" : string.IsNullOrEmpty(_library.path) ? "Library" : _library.path);
        _help.text = address ? "Stick: choose key · Trigger: type · B: back" :
            "Stick: choose · Trigger: open · B: back/cancel · Y: show/hide";
        _detail.text = _mode == Mode.Pair && !string.IsNullOrEmpty(_candidateFingerprint)
            ? "Compare with 'Server certificate SHA-256' in the server log:\n\n" + GroupFingerprint(_candidateFingerprint)
            : address ? "https://" + _addressEntry : "";
        Rect(_detail.gameObject, 30, address ? -125 : -220, 860, address ? 70 : 320);
        _detail.gameObject.SetActive(_mode != Mode.Browse);
        UpdatePreview();
    }

    void DrawKeyboard()
    {
        _detail.text = "https://" + _addressEntry;
        for (int i = 0; i < Keys.Length; i++)
            _keyBgs[i].color = i == _keySelected ? new Color(0.18f, 0.38f, 0.78f, 0.95f) : new Color(1, 1, 1, 0.08f);
    }

    void UpdatePreview()
    {
        string url = null;
        if (_mode == Mode.Browse && _selected >= 0 && _selected < _entries.Count &&
            _entries[_selected].kind == EntryKind.Scene)
            url = _origin + _entries[_selected].scene.preview_url;
        if (url == _previewUrl) return;
        _previewUrl = url;
        _previewRequest?.Abort();
        if (_previewTexture != null) Destroy(_previewTexture);
        _previewTexture = null;
        _preview.texture = null;
        _preview.gameObject.SetActive(url != null);
        if (url != null && !string.IsNullOrEmpty(VRPhotoCertificate.Saved(_origin)))
            StartCoroutine(LoadPreview(url));
    }

    IEnumerator LoadPreview(string url)
    {
        using (var request = UnityWebRequestTexture.GetTexture(url))
        {
            _previewRequest = request;
            var handler = new VRPhotoCertificateHandler(VRPhotoCertificate.Saved(_origin));
            request.certificateHandler = handler;
            yield return request.SendWebRequest();
            _previewRequest = null;
            if (CertificateChanged(handler)) yield break;
            if (request.result == UnityWebRequest.Result.Success && url == _previewUrl)
            {
                _previewTexture = DownloadHandlerTexture.GetContent(request);
                _preview.texture = _previewTexture;
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
        _panel = new GameObject("VRPhoto Catalog", typeof(RectTransform));
        _panel.transform.SetParent(transform, false);
        var canvas = _panel.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        _panel.AddComponent<CanvasScaler>();
        var panelRect = _panel.GetComponent<RectTransform>();
        panelRect.sizeDelta = new Vector2(920, 650);
        _panel.transform.localScale = Vector3.one * 0.0015f;
        var background = Child(_panel.transform, "Background");
        Rect(background, 0, 0, 920, 650);
        background.AddComponent<Image>().color = new Color(0.06f, 0.08f, 0.13f, 0.97f);
        _title = Label(background.transform, "Title", 31, 20, -16, 880, 44);
        _status = Label(background.transform, "Status", 19, 20, -59, 880, 40);
        _help = Label(background.transform, "Help", 17, 20, -612, 880, 30);
        _rowTexts = new Text[VisibleRows];
        _rowBgs = new Image[VisibleRows];
        for (int i = 0; i < VisibleRows; i++)
        {
            var row = Child(background.transform, "Row " + i);
            Rect(row, 20, -105 - i * 44, 590, 42);
            _rowBgs[i] = row.AddComponent<Image>();
            _rowTexts[i] = Label(row.transform, "Text", 21, 12, -1, 566, 42);
        }
        _detail = Label(background.transform, "Details", 23, 30, -118, 860, 360);
        _detail.alignment = TextAnchor.UpperLeft;
        _detail.horizontalOverflow = HorizontalWrapMode.Wrap;
        _preview = Child(background.transform, "Preview").AddComponent<RawImage>();
        Rect(_preview.gameObject, 626, -124, 274, 210);
        _preview.color = Color.white;
        _keyTexts = new Text[Keys.Length];
        _keyBgs = new Image[Keys.Length];
        for (int i = 0; i < Keys.Length; i++)
        {
            var key = Child(background.transform, "Key " + i);
            Rect(key, 30 + i % KeyColumns * 56, -206 - i / KeyColumns * 57, 52, 50);
            _keyBgs[i] = key.AddComponent<Image>();
            _keyTexts[i] = Label(key.transform, "Label", 20, 0, 0, 52, 50);
            _keyTexts[i].alignment = TextAnchor.MiddleCenter;
            _keyTexts[i].text = Keys[i];
        }
        _panel.SetActive(false);
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
