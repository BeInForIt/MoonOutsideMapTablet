using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

// UI aliases (avoid conflicts with System.*)
using UIText = UnityEngine.UI.Text;
using UIImage = UnityEngine.UI.Image;
using UIRawImage = UnityEngine.UI.RawImage;
using UICanvasScaler = UnityEngine.UI.CanvasScaler;
using UIGraphicsRaycaster = UnityEngine.UI.GraphicRaycaster;

namespace MoonOutsideMapTablet
{
    /*
     * Moon Outside Map Tablet
     * Shows a schematic outside map (ship + entrances) in a tablet-style UI.
     * Designed to work with vanilla and most modded moons.
     */
    [BepInPlugin(ModGuid, ModName, ModVersion)]
    public class MoonOutsideMapTabletPlugin : BaseUnityPlugin
    {
        public const string ModGuid = "hoppinhauler_moonoutsidemaptablet";
        public const string ModName = "Moon Outside Map Tablet";
        public const string ModVersion = "1.0.0";

        private Harmony _harmony;

        // ================= CONFIG =================

        private ConfigEntry<KeyCode> _toggleKey;
        private ConfigEntry<int> _mapSizePx;
        private ConfigEntry<float> _metersPerPixel;
        private ConfigEntry<float> _dotRadiusPx;
        private ConfigEntry<float> _rotationOffsetDeg;

        // ================= UI =================

        private GameObject _canvasGo;
        private GameObject _panelGo;
        private UIRawImage _mapImage;
        private UIText _legendText;
        private UIText _hintText;

        // ================= MAP DATA =================

        private Texture2D _tex;

        private Vector3? _shipPos;
        private Transform _shipTr;

        private readonly List<MapPoint> _points = new List<MapPoint>();
        private bool _visible;

        // ================= LIFECYCLE =================

        private void Awake()
        {
            // User config
            _toggleKey = Config.Bind("Input", "ToggleKey", KeyCode.M, "Key to open/close the map tablet");
            _mapSizePx = Config.Bind("UI", "MapSizePx", 512, "Map texture size (256..1024)");
            _metersPerPixel = Config.Bind("Map", "MetersPerPixel", 1.2f, "World meters per pixel");
            _dotRadiusPx = Config.Bind("Map", "DotRadiusPx", 4f, "Entrance dot radius");
            _rotationOffsetDeg = Config.Bind(
                "Map",
                "RotationOffsetDeg",
                -90f,
                "Map rotation offset. 90 = rotate left, -90 = rotate right"
            );

            _harmony = new Harmony(ModGuid);
            _harmony.PatchAll();

            SafeEnsureUI();
            Logger.LogInfo(ModName + " loaded");
        }

        private void OnDestroy()
        {
            try { _harmony.UnpatchSelf(); } catch { }
        }

        private void Update()
        {
            // UI can disappear on scene reloads
            if (!IsUIReady())
                SafeEnsureUI();

            if (!IsUIReady())
                return;

            // Do not open while typing in terminal / UI fields
            if (!ShouldIgnoreHotkey() && IsTogglePressed())
            {
                _visible = !_visible;
                _panelGo.SetActive(_visible);

                if (_visible)
                    RefreshAndRedraw();
            }
        }

        // ================= INPUT =================

        /// <summary>
        /// Uses the new Unity Input System (required by Lethal Company)
        /// </summary>
        private bool IsTogglePressed()
        {
            var kb = Keyboard.current;
            if (kb == null) return false;

            Key key;
            if (!Enum.TryParse(_toggleKey.Value.ToString(), out key))
                return false;

            var control = kb[key];
            return control != null && control.wasPressedThisFrame;
        }

        /// <summary>
        /// Prevents opening the map while typing in Terminal or UI input fields
        /// </summary>
        private bool ShouldIgnoreHotkey()
        {
            if (Cursor.lockState != CursorLockMode.Locked)
                return true;

            var es = EventSystem.current;
            if (es == null) return false;

            var go = es.currentSelectedGameObject;
            if (go == null) return false;

            // Unity UI InputField
            if (go.GetComponent<UnityEngine.UI.InputField>() != null)
                return true;

            // TMP_InputField (no direct dependency)
            foreach (var c in go.GetComponents<Component>())
                if (c != null && c.GetType().Name == "TMP_InputField")
                    return true;

            return false;
        }

        // ================= UI CREATION =================

        private bool IsUIReady()
        {
            return _canvasGo && _panelGo && _mapImage && _legendText;
        }

        private void SafeEnsureUI()
        {
            try { CreateUI(); }
            catch (Exception e) { Logger.LogError(e); }
        }

        private void CreateUI()
        {
            if (_canvasGo != null) return;

            _canvasGo = new GameObject("MoonOutsideMapTablet_Canvas");
            DontDestroyOnLoad(_canvasGo);

            var canvas = _canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvasGo.AddComponent<UICanvasScaler>();
            _canvasGo.AddComponent<UIGraphicsRaycaster>();

            _panelGo = new GameObject("Panel");
            _panelGo.transform.SetParent(_canvasGo.transform, false);

            var bg = _panelGo.AddComponent<UIImage>();
            bg.color = new Color(0f, 0f, 0f, 0.75f);

            var prt = _panelGo.GetComponent<RectTransform>();
            prt.sizeDelta = new Vector2(700, 620);
            prt.anchoredPosition = Vector2.zero;

            // Map image
            var mapGo = new GameObject("Map");
            mapGo.transform.SetParent(_panelGo.transform, false);
            _mapImage = mapGo.AddComponent<UIRawImage>();
            mapGo.GetComponent<RectTransform>().sizeDelta = new Vector2(560, 560);

            // Legend
            _legendText = CreateText("Legend", new Vector2(560, 140), TextAnchor.UpperLeft, -260);

            // Hint
            _hintText = CreateText("Hint", new Vector2(320, 30), TextAnchor.UpperRight, 280);
            _hintText.fontSize = 16;

            _panelGo.SetActive(false);
        }

        private UIText CreateText(string name, Vector2 size, TextAnchor anchor, float yOffset)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_panelGo.transform, false);

            var t = go.AddComponent<UIText>();
            t.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            t.fontSize = 18;
            t.color = Color.white;
            t.alignment = anchor;

            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = size;
            rt.anchoredPosition = new Vector2(0, yOffset);

            return t;
        }

        // ================= MAP LOGIC =================

        private void EnsureTexture()
        {
            int size = Mathf.Clamp(_mapSizePx.Value, 256, 1024);
            if (_tex != null && _tex.width == size) return;

            _tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            _tex.filterMode = FilterMode.Point;
            _mapImage.texture = _tex;
        }

        private void RefreshAndRedraw()
        {
            EnsureTexture();
            RefreshPoints();
            DrawMap();
        }

        /// <summary>
        /// Finds ship and outside entrances (Main + Fire)
        /// Works for vanilla and most modded moons
        /// </summary>
        private void RefreshPoints()
        {
            _points.Clear();
            _shipTr = null;
            _shipPos = null;

            var ship = FindShipAnchor();
            if (ship != null)
            {
                _shipTr = ship.transform;
                _shipPos = _shipTr.position;
            }

            foreach (var mb in FindObjectsOfType<MonoBehaviour>())
            {
                if (mb == null) continue;
                if (mb.GetType().Name != "EntranceTeleport") continue;

                // Filter: only outside entrances
                bool? outside = TryGetBool(mb, "isEntranceToBuilding");
                if (outside.HasValue && outside.Value == false)
                    continue;

                EntranceKind kind = EntranceKind.Unknown;

                int? id = TryGetInt(mb, "entranceId");
                if (id == 0) kind = EntranceKind.Main;
                else if (id == 1) kind = EntranceKind.FireExit;

                _points.Add(new MapPoint(kind, mb.transform.position));
            }

            _legendText.text =
                $"Ship: {(_shipPos.HasValue ? "OK" : "NOT FOUND")}\n" +
                $"Entrances: {_points.Count}\n" +
                $"Scale: 1px = {_metersPerPixel.Value:0.0}m\n" +
                $"Rotation: {_rotationOffsetDeg.Value:0}°";

            _hintText.text = $"Toggle: {_toggleKey.Value}";
        }

        private GameObject FindShipAnchor()
        {
            foreach (var go in FindObjectsOfType<GameObject>())
                if (go && go.name.IndexOf("Terminal", StringComparison.OrdinalIgnoreCase) >= 0)
                    return go.transform.root.gameObject;

            return null;
        }

        private void DrawMap()
        {
            int w = _tex.width;
            int h = _tex.height;
            int cx = w / 2;
            int cy = h / 2;

            // background
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    _tex.SetPixel(x, y, (x % 32 == 0 || y % 32 == 0)
                        ? new Color(0.15f, 0.15f, 0.15f)
                        : new Color(0.09f, 0.09f, 0.1f));

            if (!_shipPos.HasValue)
            {
                DrawDot(cx, cy, 6, Color.yellow);
                _tex.Apply();
                return;
            }

            DrawDot(cx, cy, 6, Color.white);

            foreach (var p in _points)
            {
                Vector2 local = WorldDeltaToShipLocal(p.Pos - _shipPos.Value);

                int px = cx + Mathf.RoundToInt(local.x / _metersPerPixel.Value);
                int py = cy + Mathf.RoundToInt(local.y / _metersPerPixel.Value);

                Color c = p.Kind == EntranceKind.Main ? Color.green :
                          p.Kind == EntranceKind.FireExit ? Color.red : Color.cyan;

                DrawDot(px, py, _dotRadiusPx.Value, c);
            }

            _tex.Apply();
        }

        private Vector2 WorldDeltaToShipLocal(Vector3 delta)
        {
            Vector3 r = _shipTr.right;
            Vector3 f = _shipTr.forward;

            float x = Vector3.Dot(delta, r);
            float y = Vector3.Dot(delta, f);

            float a = _rotationOffsetDeg.Value * Mathf.Deg2Rad;
            return new Vector2(
                x * Mathf.Cos(a) - y * Mathf.Sin(a),
                x * Mathf.Sin(a) + y * Mathf.Cos(a)
            );
        }

        private void DrawDot(int x, int y, float r, Color c)
        {
            int rr = Mathf.CeilToInt(r);
            for (int dy = -rr; dy <= rr; dy++)
                for (int dx = -rr; dx <= rr; dx++)
                    if (dx * dx + dy * dy <= r * r &&
                        x + dx >= 0 && x + dx < _tex.width &&
                        y + dy >= 0 && y + dy < _tex.height)
                        _tex.SetPixel(x + dx, y + dy, c);
        }

        // ================= REFLECTION HELPERS =================

        private static bool? TryGetBool(object o, string n)
        {
            var f = o.GetType().GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null && f.FieldType == typeof(bool)) return (bool)f.GetValue(o);
            return null;
        }

        private static int? TryGetInt(object o, string n)
        {
            var f = o.GetType().GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null) return Convert.ToInt32(f.GetValue(o));
            return null;
        }

        // ================= DATA =================

        private struct MapPoint
        {
            public EntranceKind Kind;
            public Vector3 Pos;
            public MapPoint(EntranceKind k, Vector3 p) { Kind = k; Pos = p; }
        }

        private enum EntranceKind { Unknown, Main, FireExit }
    }
}
