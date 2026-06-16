using System;
using System.Collections.Generic;
using System.Linq;
using UnityAI.ControlPlane.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace UnityAI.ControlPlane.Editor
{
    [Serializable]
    public sealed class UiThemeInput
    {
        public string background = "#0F172AF2";
        public string panel = "#111827E6";
        public string primary = "#2563EBFF";
        public string secondary = "#334155FF";
        public string accent = "#F59E0BFF";
        public string text = "#F8FAFCFF";
        public string mutedText = "#CBD5E1FF";
        public string danger = "#DC2626FF";
        public int baseFontSize = 28;
        public int titleFontSize = 72;
        public int buttonFontSize = 30;
        public float safeAreaMargin = 80f;
    }

    [Serializable]
    public sealed class UiButtonInput
    {
        public string name;
        public string text;
        public string actionId;
        public string intent;
        public string variant = "primary";
    }

    [Serializable]
    public sealed class UiStatInput
    {
        public string name;
        public string label;
        public string value;
    }

    [Serializable]
    public sealed class UiLabelInput
    {
        public string name;
        public string text;
        public string slot = "body";
        public int fontSize;
    }

    [Serializable]
    public sealed class UiComposeInput
    {
        public bool dryRun = true;
        public bool confirm;
        public string mode = "upsert";
        public string template = "main_menu";
        public string canvasName = "Unity AI UI Canvas";
        public string screenName = "Unity AI Screen";
        public string screenId = "unity-ai-screen";
        public string title = "New Screen";
        public string subtitle = string.Empty;
        public string body = string.Empty;
        public int referenceWidth = 1920;
        public int referenceHeight = 1080;
        public float matchWidthOrHeight = 0.5f;
        public bool ensureEventSystem = true;
        public bool createActionMarkers = true;
        public bool enforceReadableContrast = true;
        public UiThemeInput theme = new();
        public UiButtonInput[] buttons = Array.Empty<UiButtonInput>();
        public UiStatInput[] stats = Array.Empty<UiStatInput>();
        public UiLabelInput[] labels = Array.Empty<UiLabelInput>();
    }

    [Serializable]
    public sealed class UiComposeRequest
    {
        public UiComposeInput input = new();
    }

    [Serializable]
    public sealed class UiAuditInput
    {
        public string pathPrefix = string.Empty;
        public bool includeInactive = true;
        public int maxElements = 200;
        public int maxFindings = 200;
        public float minContrastRatio = 4.5f;
    }

    [Serializable]
    public sealed class UiAuditRequest
    {
        public UiAuditInput input = new();
    }

    [Serializable]
    public sealed class UiFinding
    {
        public string severity;
        public string code;
        public string path;
        public string message;
        public string fixHint;
    }

    [Serializable]
    public sealed class UiCanvasInfo
    {
        public string path;
        public string renderMode;
        public bool hasCanvasScaler;
        public string canvasScalerMode;
        public int referenceWidth;
        public int referenceHeight;
        public bool hasGraphicRaycaster;
        public int uiElementCount;
    }

    [Serializable]
    public sealed class UiElementInfo
    {
        public string path;
        public string kind;
        public string text;
        public string actionId;
        public bool activeInHierarchy;
        public bool interactable;
        public float width;
        public float height;
        public string color;
    }

    [Serializable]
    public sealed class UiAuditReport
    {
        public string scenePath;
        public string sceneName;
        public int canvasCount;
        public int eventSystemCount;
        public int inputModuleCount;
        public int uiElementCount;
        public int interactiveElementCount;
        public int textElementCount;
        public int markerCount;
        public int findingCount;
        public int errorCount;
        public int warningCount;
        public int infoCount;
        public int qualityScore;
        public bool truncated;
        public UiCanvasInfo[] canvases = Array.Empty<UiCanvasInfo>();
        public UiElementInfo[] elements = Array.Empty<UiElementInfo>();
        public UiFinding[] findings = Array.Empty<UiFinding>();
        public string[] recommendedActions = Array.Empty<string>();
        public string verificationStatus;
        public string[] verificationSignals = Array.Empty<string>();
        public string capturedAtUtc;
    }

    [Serializable]
    public sealed class UiComposeResult
    {
        public bool dryRun;
        public bool applied;
        public bool refused;
        public bool rolledBack;
        public bool requiresConfirmation;
        public string requestId;
        public string correlationId;
        public string scenePath;
        public string canvasPath;
        public string screenPath;
        public string checkpointId;
        public string template;
        public string mode;
        public string message;
        public string verificationStatus;
        public string[] verificationSignals = Array.Empty<string>();
        public string[] requiredPermissions = Array.Empty<string>();
        public string[] warnings = Array.Empty<string>();
        public UiAuditReport audit;
        public UnityAiAuditEvent[] auditEvents = Array.Empty<UnityAiAuditEvent>();
        public bool auditPersisted;
        public string auditLogPath;
        public string timestampUtc;
    }

    public static class UiComposeOperation
    {
        private const string ComposeCapability = "unity.ui.compose";
        private const int MaxButtons = 12;
        private const int MaxStats = 12;
        private const int MaxLabels = 24;
        private const int MaxNameLength = 80;
        private const int MaxTextLength = 400;

        private static readonly HashSet<string> AllowedTemplates = new(StringComparer.Ordinal)
        {
            "main_menu",
            "pause_menu",
            "hud",
            "dialog",
            "blank"
        };

        private static readonly HashSet<string> AllowedModes = new(StringComparer.Ordinal)
        {
            "create",
            "upsert",
            "replace"
        };

        public static UiComposeResult Compose(string requestBody)
        {
            var request = ParseComposeRequest(requestBody);
            var input = NormalizeInput(request.input ?? new UiComposeInput());
            var envelope = UnityAiJobStore.ParseEnvelope(requestBody);
            var scene = EditorSceneManager.GetActiveScene();
            var warnings = new List<string>();

            if (!scene.IsValid())
            {
                return BuildComposeResult(envelope, input, false, true, false, false, string.Empty, string.Empty, string.Empty, "No valid active scene is available.", "refused", warnings, null);
            }

            if (!ValidateInput(input, out var refusal))
            {
                return BuildComposeResult(envelope, input, false, true, false, false, string.Empty, string.Empty, string.Empty, refusal, "refused", warnings, null);
            }

            var canvasPath = input.canvasName;
            var screenPath = canvasPath + "/" + input.screenName;

            if (input.dryRun)
            {
                warnings.AddRange(PreviewWarnings(input));
                var auditPreview = AuditInternal(new UiAuditInput { pathPrefix = canvasPath, includeInactive = true, maxElements = 100, maxFindings = 100, minContrastRatio = 4.5f });
                return BuildComposeResult(envelope, input, false, false, false, false, canvasPath, screenPath, string.Empty, $"DRY RUN: validated UI {input.template} screen '{screenPath}'.", "passed", warnings, auditPreview);
            }

            if (!input.confirm)
            {
                return BuildComposeResult(envelope, input, false, false, false, true, canvasPath, screenPath, string.Empty, $"CONFIRMATION REQUIRED: would compose UI screen '{screenPath}'.", "needs_confirmation", warnings, null);
            }

            var checkpointId = string.Empty;
            if (!string.IsNullOrWhiteSpace(scene.path))
            {
                checkpointId = DurableCheckpointStore.CreateInternal("ui-compose", new[] { scene.path, scene.path + ".meta" }).checkpointId;
            }
            else
            {
                warnings.Add("Active scene is unsaved; Unity Undo rollback is available, but no durable scene checkpoint was created.");
            }

            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Unity AI Compose UI");

            try
            {
                var canvas = EnsureCanvas(input);
                var existingScreen = FindChild(canvas.gameObject, input.screenName);
                if (input.mode == "create" && existingScreen != null)
                {
                    throw new InvalidOperationException($"UI screen '{screenPath}' already exists and mode=create was requested.");
                }

                if (existingScreen != null && input.mode == "replace")
                {
                    Undo.DestroyObjectImmediate(existingScreen);
                    existingScreen = null;
                }

                var screen = existingScreen != null
                    ? existingScreen
                    : CreateUiObject(input.screenName, canvas.transform);
                screen.layer = ResolveUiLayer();
                ClearChildren(screen.transform);
                ConfigureScreen(screen, input);

                if (input.ensureEventSystem && HasInteractiveContent(input))
                {
                    EnsureEventSystem(warnings);
                }

                EditorSceneManager.MarkSceneDirty(scene);
                var audit = AuditInternal(new UiAuditInput
                {
                    pathPrefix = GetGameObjectPath(screen),
                    includeInactive = true,
                    maxElements = 200,
                    maxFindings = 200,
                    minContrastRatio = 4.5f
                });

                if (audit.errorCount > 0)
                {
                    Undo.RevertAllDownToGroup(undoGroup);
                    return BuildComposeResult(envelope, input, false, false, true, false, canvasPath, screenPath, checkpointId, "UI composition failed quality gate and was rolled back.", "failed", warnings, audit);
                }

                Undo.CollapseUndoOperations(undoGroup);
                return BuildComposeResult(envelope, input, true, false, false, false, GetGameObjectPath(canvas.gameObject), GetGameObjectPath(screen), checkpointId, $"Composed and verified UI {input.template} screen '{GetGameObjectPath(screen)}'.", audit.warningCount > 0 ? "warning" : "passed", warnings, audit);
            }
            catch (Exception exception)
            {
                Undo.RevertAllDownToGroup(undoGroup);
                return BuildComposeResult(envelope, input, false, false, true, false, canvasPath, screenPath, checkpointId, $"UI composition failed and was rolled back: {exception.GetBaseException().Message}", "failed", warnings, null);
            }
        }

        public static UiAuditReport Audit(string requestBody)
        {
            return AuditInternal(ParseAuditRequest(requestBody).input ?? new UiAuditInput());
        }

        private static void ConfigureScreen(GameObject screen, UiComposeInput input)
        {
            var theme = input.theme ?? new UiThemeInput();
            var background = ParseColor(theme.background, "#0F172AF2");
            var panel = ParseColor(theme.panel, "#111827E6");
            var primary = ParseColor(theme.primary, "#2563EBFF");
            var secondary = ParseColor(theme.secondary, "#334155FF");
            var accent = ParseColor(theme.accent, "#F59E0BFF");
            var text = ParseColor(theme.text, "#F8FAFCFF");
            var muted = ParseColor(theme.mutedText, "#CBD5E1FF");
            var danger = ParseColor(theme.danger, "#DC2626FF");
            if (input.enforceReadableContrast)
            {
                text = EnsureReadable(text, panel, 4.5f);
                muted = EnsureReadable(muted, panel, 3f);
            }

            var screenRect = screen.GetComponent<RectTransform>();
            Stretch(screenRect, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            var screenImage = GetOrAddComponent<Image>(screen);
            screenImage.color = background;
            screenImage.raycastTarget = true;

            var marker = GetOrAddComponent<UnityAiUiScreenMarker>(screen);
            marker.screenId = input.screenId;
            marker.template = input.template;
            marker.title = input.title;
            marker.qualityProfile = "production";
            EditorUtility.SetDirty(marker);

            var safeArea = CreateUiObject("SafeArea", screen.transform);
            Stretch(safeArea.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, new Vector2(theme.safeAreaMargin, theme.safeAreaMargin), new Vector2(-theme.safeAreaMargin, -theme.safeAreaMargin));

            switch (input.template)
            {
                case "hud":
                    BuildHud(safeArea.transform, input, text, muted, panel, accent);
                    break;
                case "dialog":
                    BuildDialog(safeArea.transform, input, text, muted, panel, primary, secondary, danger);
                    break;
                case "pause_menu":
                    BuildMenu(safeArea.transform, input, text, muted, panel, primary, secondary, danger, true);
                    break;
                case "blank":
                    BuildBlank(safeArea.transform, input, text, muted, panel);
                    break;
                default:
                    BuildMenu(safeArea.transform, input, text, muted, panel, primary, secondary, danger, false);
                    break;
            }
        }

        private static void BuildMenu(Transform parent, UiComposeInput input, Color text, Color muted, Color panel, Color primary, Color secondary, Color danger, bool pause)
        {
            var card = CreatePanel("MenuCard", parent, panel, new Vector2(0.5f, 0.5f), new Vector2(900, pause ? 680 : 760), new Vector2(0, 0));
            var layout = GetOrAddComponent<VerticalLayoutGroup>(card);
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.spacing = 24;
            layout.padding = new RectOffset(64, 64, 56, 56);
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            CreateText("Title", card.transform, NonEmpty(input.title, pause ? "Paused" : "Main Menu"), input.theme.titleFontSize, FontStyle.Bold, text, TextAnchor.MiddleCenter, 110);
            if (!string.IsNullOrWhiteSpace(input.subtitle))
            {
                CreateText("Subtitle", card.transform, TrimText(input.subtitle), input.theme.baseFontSize, FontStyle.Normal, muted, TextAnchor.MiddleCenter, 70);
            }

            var buttons = NormalizeButtons(input, pause ? DefaultPauseButtons() : DefaultMenuButtons());
            foreach (var button in buttons)
            {
                CreateButton(button, card.transform, input, ButtonColor(button, primary, secondary, danger), text);
            }

            CreateExtraLabels(card.transform, input, muted);
        }

        private static void BuildHud(Transform parent, UiComposeInput input, Color text, Color muted, Color panel, Color accent)
        {
            var topBar = CreatePanel("TopBar", parent, panel, new Vector2(0.5f, 1f), new Vector2(0, 96), new Vector2(0, -48));
            Stretch(topBar.GetComponent<RectTransform>(), new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -96), Vector2.zero);
            var horizontal = GetOrAddComponent<HorizontalLayoutGroup>(topBar);
            horizontal.childAlignment = TextAnchor.MiddleCenter;
            horizontal.spacing = 32;
            horizontal.padding = new RectOffset(32, 32, 8, 8);
            horizontal.childControlWidth = true;
            horizontal.childForceExpandWidth = false;

            var title = CreateText("HudTitle", topBar.transform, NonEmpty(input.title, "HUD"), input.theme.baseFontSize, FontStyle.Bold, text, TextAnchor.MiddleLeft, 80);
            GetOrAddComponent<LayoutElement>(title).preferredWidth = 420;

            var stats = NormalizeStats(input);
            foreach (var stat in stats)
            {
                var statText = CreateText(SafeName(NonEmpty(stat.name, stat.label), "Stat"), topBar.transform, $"{TrimText(stat.label)}: {TrimText(stat.value)}", input.theme.baseFontSize - 4, FontStyle.Bold, accent, TextAnchor.MiddleCenter, 80);
                GetOrAddComponent<LayoutElement>(statText).preferredWidth = 260;
            }

            if (!string.IsNullOrWhiteSpace(input.body))
            {
                var objective = CreatePanel("ObjectivePanel", parent, panel, new Vector2(0.5f, 0f), new Vector2(920, 92), new Vector2(0, 70));
                CreateText("ObjectiveText", objective.transform, TrimText(input.body), input.theme.baseFontSize - 2, FontStyle.Normal, muted, TextAnchor.MiddleCenter, 70);
            }

            CreateExtraLabels(parent, input, muted);
        }

        private static void BuildDialog(Transform parent, UiComposeInput input, Color text, Color muted, Color panel, Color primary, Color secondary, Color danger)
        {
            var card = CreatePanel("DialogCard", parent, panel, new Vector2(0.5f, 0.5f), new Vector2(980, 560), Vector2.zero);
            var layout = GetOrAddComponent<VerticalLayoutGroup>(card);
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.spacing = 22;
            layout.padding = new RectOffset(60, 60, 48, 48);
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            CreateText("DialogTitle", card.transform, NonEmpty(input.title, "Dialog"), input.theme.titleFontSize - 18, FontStyle.Bold, text, TextAnchor.MiddleCenter, 90);
            CreateText("DialogBody", card.transform, NonEmpty(input.body, input.subtitle), input.theme.baseFontSize, FontStyle.Normal, muted, TextAnchor.MiddleCenter, 170);

            var row = CreateUiObject("Actions", card.transform);
            var rowRect = row.GetComponent<RectTransform>();
            rowRect.sizeDelta = new Vector2(860, 96);
            var rowLayout = GetOrAddComponent<HorizontalLayoutGroup>(row);
            rowLayout.spacing = 24;
            rowLayout.childControlWidth = true;
            rowLayout.childForceExpandWidth = true;
            rowLayout.childControlHeight = true;
            rowLayout.childForceExpandHeight = true;

            foreach (var button in NormalizeButtons(input, DefaultDialogButtons()))
            {
                CreateButton(button, row.transform, input, ButtonColor(button, primary, secondary, danger), text);
            }

            CreateExtraLabels(card.transform, input, muted);
        }

        private static void BuildBlank(Transform parent, UiComposeInput input, Color text, Color muted, Color panel)
        {
            var card = CreatePanel("ContentPanel", parent, panel, new Vector2(0.5f, 0.5f), new Vector2(1000, 640), Vector2.zero);
            var layout = GetOrAddComponent<VerticalLayoutGroup>(card);
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.spacing = 18;
            layout.padding = new RectOffset(64, 64, 56, 56);
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            CreateText("Title", card.transform, NonEmpty(input.title, "UI Screen"), input.theme.titleFontSize - 10, FontStyle.Bold, text, TextAnchor.MiddleCenter, 100);
            if (!string.IsNullOrWhiteSpace(input.body) || !string.IsNullOrWhiteSpace(input.subtitle))
            {
                CreateText("Body", card.transform, NonEmpty(input.body, input.subtitle), input.theme.baseFontSize, FontStyle.Normal, muted, TextAnchor.MiddleCenter, 240);
            }

            CreateExtraLabels(card.transform, input, muted);
        }

        private static GameObject CreatePanel(string name, Transform parent, Color color, Vector2 anchor, Vector2 size, Vector2 anchoredPosition)
        {
            var panel = CreateUiObject(name, parent);
            var rect = panel.GetComponent<RectTransform>();
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = anchor;
            rect.sizeDelta = size;
            rect.anchoredPosition = anchoredPosition;
            var image = GetOrAddComponent<Image>(panel);
            image.color = color;
            image.raycastTarget = true;
            return panel;
        }

        private static GameObject CreateText(string name, Transform parent, string value, int fontSize, FontStyle style, Color color, TextAnchor alignment, float preferredHeight)
        {
            var textObject = CreateUiObject(name, parent);
            var text = GetOrAddComponent<Text>(textObject);
            text.text = TrimText(value);
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.fontSize = Mathf.Clamp(fontSize, 12, 160);
            text.fontStyle = style;
            text.alignment = alignment;
            text.color = color;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.raycastTarget = false;
            var layout = GetOrAddComponent<LayoutElement>(textObject);
            layout.preferredHeight = preferredHeight;
            layout.minHeight = Mathf.Min(preferredHeight, 48);
            return textObject;
        }

        private static GameObject CreateButton(UiButtonInput input, Transform parent, UiComposeInput composeInput, Color background, Color textColor)
        {
            var buttonObject = CreateUiObject(SafeName(NonEmpty(input.name, input.text), "Button"), parent);
            var image = GetOrAddComponent<Image>(buttonObject);
            image.color = background;
            image.raycastTarget = true;

            var button = GetOrAddComponent<Button>(buttonObject);
            button.targetGraphic = image;
            button.interactable = true;
            var colors = button.colors;
            colors.normalColor = background;
            colors.highlightedColor = Color.Lerp(background, Color.white, 0.16f);
            colors.pressedColor = Color.Lerp(background, Color.black, 0.18f);
            colors.selectedColor = colors.highlightedColor;
            colors.disabledColor = new Color(background.r, background.g, background.b, 0.45f);
            button.colors = colors;

            var layout = GetOrAddComponent<LayoutElement>(buttonObject);
            layout.preferredHeight = 74;
            layout.minHeight = 64;

            var label = CreateText("Label", buttonObject.transform, NonEmpty(input.text, "Button"), composeInput.theme.buttonFontSize, FontStyle.Bold, textColor, TextAnchor.MiddleCenter, 72);
            Stretch(label.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, new Vector2(24, 0), new Vector2(-24, 0));

            if (composeInput.createActionMarkers)
            {
                var marker = GetOrAddComponent<UnityAiUiActionMarker>(buttonObject);
                marker.actionId = NonEmpty(input.actionId, SafeId(input.text));
                marker.label = NonEmpty(input.text, "Button");
                marker.intent = TrimText(input.intent);
                EditorUtility.SetDirty(marker);
            }

            return buttonObject;
        }

        private static void CreateExtraLabels(Transform parent, UiComposeInput input, Color color)
        {
            if (input.labels == null || input.labels.Length == 0)
            {
                return;
            }

            for (var index = 0; index < input.labels.Length; index++)
            {
                var label = input.labels[index];
                if (label == null || string.IsNullOrWhiteSpace(label.text))
                {
                    continue;
                }

                var name = SafeName(NonEmpty(label.name, label.slot, "Label " + (index + 1)), "Label");
                var size = label.fontSize > 0 ? label.fontSize : Math.Max(12, input.theme.baseFontSize - 4);
                CreateText(name, parent, label.text, size, FontStyle.Normal, color, TextAnchor.MiddleCenter, 64);
            }
        }

        private static Canvas EnsureCanvas(UiComposeInput input)
        {
            var canvasObject = FindRoot(input.canvasName);
            if (canvasObject == null)
            {
                canvasObject = new GameObject(input.canvasName, typeof(RectTransform));
                Undo.RegisterCreatedObjectUndo(canvasObject, "Unity AI Create UI Canvas");
            }

            canvasObject.layer = ResolveUiLayer();
            var canvas = GetOrAddComponent<Canvas>(canvasObject);
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.pixelPerfect = false;

            var scaler = GetOrAddComponent<CanvasScaler>(canvasObject);
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(input.referenceWidth, input.referenceHeight);
            scaler.matchWidthOrHeight = Mathf.Clamp01(input.matchWidthOrHeight);

            GetOrAddComponent<GraphicRaycaster>(canvasObject);
            return canvas;
        }

        private static void EnsureEventSystem(List<string> warnings)
        {
            var eventSystems = FindObjects<EventSystem>(true)
                .Where(system => system.gameObject.scene == EditorSceneManager.GetActiveScene())
                .ToArray();
            var eventSystem = eventSystems.FirstOrDefault();
            if (eventSystem == null)
            {
                var eventSystemObject = new GameObject("EventSystem");
                Undo.RegisterCreatedObjectUndo(eventSystemObject, "Unity AI Create EventSystem");
                eventSystem = Undo.AddComponent<EventSystem>(eventSystemObject);
            }

            if (eventSystems.Length > 1)
            {
                warnings.Add("Multiple EventSystem objects exist; UI input may be ambiguous.");
            }

            if (eventSystem.GetComponents<BaseInputModule>().Length == 0)
            {
                Undo.AddComponent<StandaloneInputModule>(eventSystem.gameObject);
                warnings.Add("Added StandaloneInputModule. If the project uses only the new Input System, replace it with InputSystemUIInputModule.");
            }
        }

        private static UiAuditReport AuditInternal(UiAuditInput rawInput)
        {
            var input = rawInput ?? new UiAuditInput();
            var maxElements = input.maxElements <= 0 ? 200 : Math.Min(input.maxElements, 1000);
            var maxFindings = input.maxFindings <= 0 ? 200 : Math.Min(input.maxFindings, 1000);
            var minContrast = input.minContrastRatio <= 0 ? 4.5f : Mathf.Clamp(input.minContrastRatio, 1f, 21f);
            var scene = EditorSceneManager.GetActiveScene();
            var prefix = NormalizePath(input.pathPrefix);
            var findings = new List<UiFinding>();
            var elements = new List<UiElementInfo>();

            var canvases = FindObjects<Canvas>(input.includeInactive)
                .Where(canvas => canvas != null && canvas.gameObject.scene == scene && MatchesPrefix(canvas.gameObject, prefix))
                .OrderBy(canvas => GetGameObjectPath(canvas.gameObject), StringComparer.Ordinal)
                .ToArray();

            var eventSystems = FindObjects<EventSystem>(input.includeInactive)
                .Where(system => system != null && system.gameObject.scene == scene)
                .ToArray();

            if (canvases.Length == 0)
            {
                AddFinding(findings, maxFindings, "error", "ui.canvas.missing", string.Empty, "No Canvas exists in the active scene.", "Create a screen-space Canvas with CanvasScaler and GraphicRaycaster.");
            }

            if (eventSystems.Length > 1)
            {
                AddFinding(findings, maxFindings, "warning", "ui.event_system.duplicate", string.Empty, "Multiple EventSystem objects exist.", "Keep one active EventSystem for predictable UI input.");
            }

            var canvasInfos = new List<UiCanvasInfo>();
            foreach (var canvas in canvases)
            {
                var canvasPath = GetGameObjectPath(canvas.gameObject);
                var scaler = canvas.GetComponent<CanvasScaler>();
                var raycaster = canvas.GetComponent<GraphicRaycaster>();
                if (canvas.renderMode != RenderMode.WorldSpace && scaler == null)
                {
                    AddFinding(findings, maxFindings, "error", "ui.canvas.scaler_missing", canvasPath, "Screen-space Canvas is missing CanvasScaler.", "Add CanvasScaler set to Scale With Screen Size.");
                }
                else if (scaler != null && canvas.renderMode != RenderMode.WorldSpace && scaler.uiScaleMode != CanvasScaler.ScaleMode.ScaleWithScreenSize)
                {
                    AddFinding(findings, maxFindings, "warning", "ui.canvas.scaler_mode", canvasPath, "CanvasScaler is not Scale With Screen Size.", "Use Scale With Screen Size for responsive UI.");
                }

                if (raycaster == null)
                {
                    AddFinding(findings, maxFindings, "warning", "ui.canvas.raycaster_missing", canvasPath, "Canvas is missing GraphicRaycaster.", "Add GraphicRaycaster if this Canvas contains interactive UI.");
                }

                canvasInfos.Add(new UiCanvasInfo
                {
                    path = canvasPath,
                    renderMode = canvas.renderMode.ToString(),
                    hasCanvasScaler = scaler != null,
                    canvasScalerMode = scaler != null ? scaler.uiScaleMode.ToString() : string.Empty,
                    referenceWidth = scaler != null ? Mathf.RoundToInt(scaler.referenceResolution.x) : 0,
                    referenceHeight = scaler != null ? Mathf.RoundToInt(scaler.referenceResolution.y) : 0,
                    hasGraphicRaycaster = raycaster != null,
                    uiElementCount = canvas.GetComponentsInChildren<RectTransform>(input.includeInactive).Length
                });
            }

            var graphics = FindObjects<Graphic>(input.includeInactive)
                .Where(graphic => graphic != null && graphic.gameObject.scene == scene && MatchesPrefix(graphic.gameObject, prefix))
                .OrderBy(graphic => GetGameObjectPath(graphic.gameObject), StringComparer.Ordinal)
                .ToArray();
            var selectables = FindObjects<Selectable>(input.includeInactive)
                .Where(selectable => selectable != null && selectable.gameObject.scene == scene && MatchesPrefix(selectable.gameObject, prefix))
                .ToArray();
            var texts = FindObjects<Text>(input.includeInactive)
                .Where(text => text != null && text.gameObject.scene == scene && MatchesPrefix(text.gameObject, prefix))
                .ToArray();
            var markers = FindObjects<UnityAiUiActionMarker>(input.includeInactive)
                .Where(marker => marker != null && marker.gameObject.scene == scene && MatchesPrefix(marker.gameObject, prefix))
                .ToArray();

            if (selectables.Length > 0)
            {
                if (eventSystems.Length == 0)
                {
                    AddFinding(findings, maxFindings, "error", "ui.event_system.missing", string.Empty, "Interactive UI exists but no EventSystem is present.", "Create an EventSystem with an input module.");
                }
                else if (eventSystems.All(system => system.GetComponents<BaseInputModule>().Length == 0))
                {
                    AddFinding(findings, maxFindings, "error", "ui.event_system.input_module_missing", GetGameObjectPath(eventSystems[0].gameObject), "EventSystem has no input module.", "Add StandaloneInputModule or InputSystemUIInputModule.");
                }
            }

            foreach (var text in texts)
            {
                var path = GetGameObjectPath(text.gameObject);
                if (string.IsNullOrWhiteSpace(text.text))
                {
                    AddFinding(findings, maxFindings, "warning", "ui.text.empty", path, "Text element is empty.", "Set meaningful visible copy or remove the element.");
                }

                if (text.fontSize < 12)
                {
                    AddFinding(findings, maxFindings, "info", "ui.text.too_small", path, "Text font size is below 12.", "Use larger text for readability.");
                }

                if (text.color.a < 0.5f)
                {
                    AddFinding(findings, maxFindings, "warning", "ui.text.low_alpha", path, "Text alpha is very low.", "Use sufficiently opaque text.");
                }

                var background = FindNearestBackground(text.transform);
                if (background.HasValue && ContrastRatio(text.color, background.Value) < minContrast)
                {
                    AddFinding(findings, maxFindings, "warning", "ui.text.low_contrast", path, "Text contrast against its nearest panel is low.", "Adjust text or panel color to meet contrast >= " + minContrast.ToString("0.0") + ".");
                }
            }

            foreach (var selectable in selectables)
            {
                var path = GetGameObjectPath(selectable.gameObject);
                if (selectable.targetGraphic == null)
                {
                    AddFinding(findings, maxFindings, "warning", "ui.selectable.target_graphic_missing", path, "Selectable has no targetGraphic.", "Assign the main Image/Graphic to targetGraphic for visual states.");
                }

                if (selectable is Button && selectable.GetComponentInChildren<Text>(true) == null)
                {
                    AddFinding(findings, maxFindings, "warning", "ui.button.label_missing", path, "Button has no Text child label.", "Add a visible label describing the action.");
                }

                if (selectable.GetComponent<UnityAiUiActionMarker>() == null)
                {
                    AddFinding(findings, maxFindings, "info", "ui.action_marker.missing", path, "Interactive element has no UnityAiUiActionMarker.", "Attach an action marker so agents can safely bind functionality later.");
                }
            }

            foreach (var graphic in graphics)
            {
                if (elements.Count >= maxElements)
                {
                    break;
                }

                var rect = graphic.rectTransform.rect;
                var path = GetGameObjectPath(graphic.gameObject);
                if ((graphic is Image || graphic is Text) && (rect.width < 1f || rect.height < 1f))
                {
                    AddFinding(findings, maxFindings, "warning", "ui.rect.zero_size", path, "UI graphic has near-zero size.", "Set a meaningful RectTransform size or layout constraints.");
                }

                var selectable = graphic.GetComponent<Selectable>();
                var marker = graphic.GetComponent<UnityAiUiActionMarker>();
                elements.Add(new UiElementInfo
                {
                    path = path,
                    kind = graphic.GetType().Name,
                    text = graphic is Text text ? text.text : string.Empty,
                    actionId = marker != null ? marker.actionId : string.Empty,
                    activeInHierarchy = graphic.gameObject.activeInHierarchy,
                    interactable = selectable != null && selectable.interactable,
                    width = rect.width,
                    height = rect.height,
                    color = "#" + ColorUtility.ToHtmlStringRGBA(graphic.color)
                });
            }

            var errors = findings.Count(finding => finding.severity == "error");
            var warnings = findings.Count(finding => finding.severity == "warning");
            var infos = findings.Count(finding => finding.severity == "info");
            var score = Mathf.Clamp(100 - errors * 25 - warnings * 8 - infos * 2, 0, 100);
            var signals = new List<string> { "structured_observation", "ui_audit_completed" };
            if (errors == 0)
            {
                signals.Add("ui_quality_gate_passed");
            }

            return new UiAuditReport
            {
                scenePath = scene.path,
                sceneName = scene.name,
                canvasCount = canvases.Length,
                eventSystemCount = eventSystems.Length,
                inputModuleCount = eventSystems.Sum(system => system.GetComponents<BaseInputModule>().Length),
                uiElementCount = graphics.Length,
                interactiveElementCount = selectables.Length,
                textElementCount = texts.Length,
                markerCount = markers.Length,
                findingCount = findings.Count,
                errorCount = errors,
                warningCount = warnings,
                infoCount = infos,
                qualityScore = score,
                truncated = graphics.Length > elements.Count || findings.Count >= maxFindings,
                canvases = canvasInfos.ToArray(),
                elements = elements.ToArray(),
                findings = findings.ToArray(),
                recommendedActions = BuildRecommendedActions(findings).ToArray(),
                verificationStatus = errors > 0 ? "failed" : warnings > 0 ? "warning" : "passed",
                verificationSignals = signals.ToArray(),
                capturedAtUtc = DateTime.UtcNow.ToString("O")
            };
        }

        private static UiComposeResult BuildComposeResult(
            UnityAiRequestEnvelope envelope,
            UiComposeInput input,
            bool applied,
            bool refused,
            bool rolledBack,
            bool requiresConfirmation,
            string canvasPath,
            string screenPath,
            string checkpointId,
            string message,
            string verificationStatus,
            List<string> warnings,
            UiAuditReport audit)
        {
            var timestamp = DateTime.UtcNow.ToString("O");
            var effects = applied || rolledBack ? new[] { "scene_change", "write_audit_log" } : new[] { "report_only", "write_audit_log" };
            var auditEvent = new UnityAiAuditEvent
            {
                timestamp = timestamp,
                capability = ComposeCapability,
                requestId = envelope.requestId,
                correlationId = envelope.correlationId,
                message = message,
                effects = effects
            };
            var auditPersisted = PersistAudit(auditEvent);
            var signals = new List<string> { "operation_audited", "structured_observation" };
            if (!string.IsNullOrWhiteSpace(checkpointId))
            {
                signals.Add("checkpoint_created");
            }

            if (applied)
            {
                signals.Add("ui_screen_composed");
                signals.Add("ui_quality_gate_passed");
                signals.Add("scene_mutation_verified");
            }

            if (rolledBack)
            {
                signals.Add("rollback_verified");
            }

            if (audit != null)
            {
                signals.Add("ui_audit_completed");
            }

            return new UiComposeResult
            {
                dryRun = input.dryRun,
                applied = applied,
                refused = refused,
                rolledBack = rolledBack,
                requiresConfirmation = requiresConfirmation,
                requestId = envelope.requestId,
                correlationId = envelope.correlationId,
                scenePath = EditorSceneManager.GetActiveScene().path,
                canvasPath = canvasPath,
                screenPath = screenPath,
                checkpointId = checkpointId,
                template = input.template,
                mode = input.mode,
                message = message,
                verificationStatus = verificationStatus,
                verificationSignals = signals.Distinct().ToArray(),
                requiredPermissions = new[] { "read_scenes", "modify_scenes" },
                warnings = warnings.ToArray(),
                audit = audit,
                auditEvents = new[] { auditEvent },
                auditPersisted = auditPersisted,
                auditLogPath = AuditLogStore.AuditLogRelativePath,
                timestampUtc = timestamp
            };
        }

        private static UiComposeInput NormalizeInput(UiComposeInput input)
        {
            input.mode = Normalize(input.mode, "upsert");
            input.template = Normalize(input.template, "main_menu");
            input.canvasName = SafeName(NonEmpty(input.canvasName, "Unity AI UI Canvas"), "Canvas");
            input.screenName = SafeName(NonEmpty(input.screenName, "Unity AI Screen"), "Screen");
            input.screenId = SafeId(NonEmpty(input.screenId, input.screenName));
            input.title = TrimText(NonEmpty(input.title, "New Screen"));
            input.subtitle = TrimText(input.subtitle);
            input.body = TrimText(input.body);
            input.referenceWidth = Mathf.Clamp(input.referenceWidth <= 0 ? 1920 : input.referenceWidth, 320, 8192);
            input.referenceHeight = Mathf.Clamp(input.referenceHeight <= 0 ? 1080 : input.referenceHeight, 240, 8192);
            input.matchWidthOrHeight = Mathf.Clamp01(input.matchWidthOrHeight);
            input.theme ??= new UiThemeInput();
            input.theme.baseFontSize = Mathf.Clamp(input.theme.baseFontSize <= 0 ? 28 : input.theme.baseFontSize, 12, 120);
            input.theme.titleFontSize = Mathf.Clamp(input.theme.titleFontSize <= 0 ? 72 : input.theme.titleFontSize, 18, 180);
            input.theme.buttonFontSize = Mathf.Clamp(input.theme.buttonFontSize <= 0 ? 30 : input.theme.buttonFontSize, 12, 120);
            input.theme.safeAreaMargin = Mathf.Clamp(input.theme.safeAreaMargin, 0, 320);
            input.buttons ??= Array.Empty<UiButtonInput>();
            input.stats ??= Array.Empty<UiStatInput>();
            input.labels ??= Array.Empty<UiLabelInput>();
            return input;
        }

        private static bool ValidateInput(UiComposeInput input, out string refusal)
        {
            refusal = string.Empty;
            if (!AllowedModes.Contains(input.mode))
            {
                refusal = "mode must be one of create, upsert, or replace.";
                return false;
            }

            if (!AllowedTemplates.Contains(input.template))
            {
                refusal = "template must be one of main_menu, pause_menu, hud, dialog, or blank.";
                return false;
            }

            if (input.buttons.Length > MaxButtons)
            {
                refusal = $"buttons cannot exceed {MaxButtons}.";
                return false;
            }

            if (input.stats.Length > MaxStats)
            {
                refusal = $"stats cannot exceed {MaxStats}.";
                return false;
            }

            if (input.labels.Length > MaxLabels)
            {
                refusal = $"labels cannot exceed {MaxLabels}.";
                return false;
            }

            return true;
        }

        private static List<string> PreviewWarnings(UiComposeInput input)
        {
            var warnings = new List<string>();
            var theme = input.theme ?? new UiThemeInput();
            var panel = ParseColor(theme.panel, "#111827E6");
            var text = ParseColor(theme.text, "#F8FAFCFF");
            if (input.enforceReadableContrast && ContrastRatio(text, panel) < 4.5f)
            {
                warnings.Add("Theme text/panel contrast is below 4.5; composition will choose safer text colors for generated controls.");
            }

            if (HasInteractiveContent(input) && !input.ensureEventSystem)
            {
                warnings.Add("Interactive content was requested but ensureEventSystem=false.");
            }

            return warnings;
        }

        private static T GetOrAddComponent<T>(GameObject gameObject) where T : Component
        {
            var component = gameObject.GetComponent<T>();
            return component != null ? component : Undo.AddComponent<T>(gameObject);
        }

        private static T[] FindObjects<T>(bool includeInactive) where T : UnityEngine.Object
        {
#if UNITY_6000_0_OR_NEWER
            return UnityEngine.Object.FindObjectsByType<T>(
                includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude);
#elif UNITY_2023_1_OR_NEWER
            return UnityEngine.Object.FindObjectsByType<T>(
                includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);
#else
            return UnityEngine.Object.FindObjectsOfType<T>(includeInactive);
#endif
        }

        private static GameObject CreateUiObject(string name, Transform parent)
        {
            var gameObject = new GameObject(SafeName(name, "UI Element"), typeof(RectTransform));
            Undo.RegisterCreatedObjectUndo(gameObject, "Unity AI Create UI Object");
            gameObject.transform.SetParent(parent, false);
            gameObject.layer = ResolveUiLayer();
            return gameObject;
        }

        private static void ClearChildren(Transform parent)
        {
            for (var index = parent.childCount - 1; index >= 0; index--)
            {
                Undo.DestroyObjectImmediate(parent.GetChild(index).gameObject);
            }
        }

        private static void Stretch(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
            rect.localScale = Vector3.one;
            rect.localRotation = Quaternion.identity;
        }

        private static UiButtonInput[] NormalizeButtons(UiComposeInput input, UiButtonInput[] fallback)
        {
            var source = input.buttons != null && input.buttons.Length > 0 ? input.buttons : fallback;
            return source
                .Where(button => button != null)
                .Select((button, index) => new UiButtonInput
                {
                    name = SafeName(NonEmpty(button.name, button.text, "Button " + (index + 1)), "Button"),
                    text = TrimText(NonEmpty(button.text, button.name, "Button")),
                    actionId = SafeId(NonEmpty(button.actionId, button.text, "action_" + (index + 1))),
                    intent = TrimText(button.intent),
                    variant = Normalize(button.variant, index == 0 ? "primary" : "secondary")
                })
                .ToArray();
        }

        private static UiStatInput[] NormalizeStats(UiComposeInput input)
        {
            var source = input.stats != null && input.stats.Length > 0
                ? input.stats
                : new[] { new UiStatInput { label = "Score", value = "0" }, new UiStatInput { label = "Time", value = "00:00" } };
            return source
                .Where(stat => stat != null)
                .Select((stat, index) => new UiStatInput
                {
                    name = SafeName(NonEmpty(stat.name, stat.label, "Stat " + (index + 1)), "Stat"),
                    label = TrimText(NonEmpty(stat.label, "Stat")),
                    value = TrimText(NonEmpty(stat.value, "0"))
                })
                .ToArray();
        }

        private static UiButtonInput[] DefaultMenuButtons()
        {
            return new[]
            {
                new UiButtonInput { name = "StartButton", text = "Start", actionId = "start_game", intent = "Start the game.", variant = "primary" },
                new UiButtonInput { name = "OptionsButton", text = "Options", actionId = "open_options", intent = "Open options.", variant = "secondary" },
                new UiButtonInput { name = "QuitButton", text = "Quit", actionId = "quit_game", intent = "Quit or return to launcher.", variant = "danger" }
            };
        }

        private static UiButtonInput[] DefaultPauseButtons()
        {
            return new[]
            {
                new UiButtonInput { name = "ResumeButton", text = "Resume", actionId = "resume_game", intent = "Resume play.", variant = "primary" },
                new UiButtonInput { name = "RestartButton", text = "Restart", actionId = "restart_level", intent = "Restart the current level.", variant = "secondary" },
                new UiButtonInput { name = "MainMenuButton", text = "Main Menu", actionId = "main_menu", intent = "Return to the main menu.", variant = "danger" }
            };
        }

        private static UiButtonInput[] DefaultDialogButtons()
        {
            return new[]
            {
                new UiButtonInput { name = "ConfirmButton", text = "Continue", actionId = "dialog_confirm", intent = "Accept dialog.", variant = "primary" },
                new UiButtonInput { name = "CancelButton", text = "Cancel", actionId = "dialog_cancel", intent = "Cancel dialog.", variant = "secondary" }
            };
        }

        private static Color ButtonColor(UiButtonInput button, Color primary, Color secondary, Color danger)
        {
            return Normalize(button.variant, "primary") switch
            {
                "danger" => danger,
                "secondary" => secondary,
                "accent" => Color.Lerp(primary, Color.white, 0.2f),
                _ => primary
            };
        }

        private static Color? FindNearestBackground(Transform transform)
        {
            var current = transform.parent;
            while (current != null)
            {
                var graphic = current.GetComponent<Graphic>();
                if (graphic != null && graphic.color.a > 0.05f)
                {
                    return graphic.color;
                }

                current = current.parent;
            }

            return null;
        }

        private static float ContrastRatio(Color foreground, Color background)
        {
            var foregroundLuminance = RelativeLuminance(foreground);
            var backgroundLuminance = RelativeLuminance(background);
            var lighter = Mathf.Max(foregroundLuminance, backgroundLuminance);
            var darker = Mathf.Min(foregroundLuminance, backgroundLuminance);
            return (lighter + 0.05f) / (darker + 0.05f);
        }

        private static Color EnsureReadable(Color foreground, Color background, float minimumContrast)
        {
            if (ContrastRatio(foreground, background) >= minimumContrast)
            {
                return foreground;
            }

            return ContrastRatio(Color.white, background) >= ContrastRatio(Color.black, background)
                ? Color.white
                : Color.black;
        }

        private static float RelativeLuminance(Color color)
        {
            static float Channel(float value)
            {
                return value <= 0.03928f ? value / 12.92f : Mathf.Pow((value + 0.055f) / 1.055f, 2.4f);
            }

            return 0.2126f * Channel(color.r) + 0.7152f * Channel(color.g) + 0.0722f * Channel(color.b);
        }

        private static Color ParseColor(string raw, string fallback)
        {
            if (!ColorUtility.TryParseHtmlString(string.IsNullOrWhiteSpace(raw) ? fallback : raw.Trim(), out var color))
            {
                ColorUtility.TryParseHtmlString(fallback, out color);
            }

            return color;
        }

        private static void AddFinding(List<UiFinding> findings, int maxFindings, string severity, string code, string path, string message, string fixHint)
        {
            if (findings.Count >= maxFindings)
            {
                return;
            }

            findings.Add(new UiFinding
            {
                severity = severity,
                code = code,
                path = path,
                message = message,
                fixHint = fixHint
            });
        }

        private static IEnumerable<string> BuildRecommendedActions(List<UiFinding> findings)
        {
            return findings
                .Where(finding => finding.severity == "error" || finding.severity == "warning")
                .Select(finding => finding.fixHint)
                .Where(action => !string.IsNullOrWhiteSpace(action))
                .Distinct()
                .Take(10);
        }

        private static bool HasInteractiveContent(UiComposeInput input)
        {
            return input.template == "main_menu"
                || input.template == "pause_menu"
                || input.template == "dialog"
                || (input.buttons != null && input.buttons.Length > 0);
        }

        private static bool MatchesPrefix(GameObject gameObject, string prefix)
        {
            return string.IsNullOrWhiteSpace(prefix)
                || GetGameObjectPath(gameObject).StartsWith(prefix, StringComparison.Ordinal);
        }

        private static GameObject FindRoot(string name)
        {
            foreach (var root in EditorSceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root.name == name)
                {
                    return root;
                }
            }

            return null;
        }

        private static GameObject FindChild(GameObject root, string childName)
        {
            var child = root.transform.Find(childName);
            return child != null ? child.gameObject : null;
        }

        private static string GetGameObjectPath(GameObject gameObject)
        {
            var names = new List<string>();
            var current = gameObject.transform;
            while (current != null)
            {
                names.Add(current.name);
                current = current.parent;
            }

            names.Reverse();
            return string.Join("/", names.ToArray());
        }

        private static string SafeName(string raw, string fallback)
        {
            var name = NonEmpty(raw, fallback).Trim();
            var sanitized = new string(name.Select(character => char.IsControl(character) || character == '/' || character == '\\' ? '-' : character).ToArray());
            sanitized = sanitized.Replace("..", "-").Trim();
            if (sanitized.Length == 0)
            {
                sanitized = fallback;
            }

            return sanitized.Length > MaxNameLength ? sanitized.Substring(0, MaxNameLength) : sanitized;
        }

        private static string SafeId(string raw)
        {
            var value = NonEmpty(raw, "action").Trim().ToLowerInvariant();
            var characters = value.Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray();
            var id = new string(characters).Trim('_');
            return string.IsNullOrWhiteSpace(id) ? "action" : id;
        }

        private static string TrimText(string raw)
        {
            var value = (raw ?? string.Empty).Trim();
            return value.Length > MaxTextLength ? value.Substring(0, MaxTextLength) : value;
        }

        private static string NonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return string.Empty;
        }

        private static string Normalize(string value, string fallback = "")
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();
        }

        private static string NormalizePath(string value)
        {
            return (value ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
        }

        private static int ResolveUiLayer()
        {
            var uiLayer = LayerMask.NameToLayer("UI");
            return uiLayer >= 0 ? uiLayer : 0;
        }

        private static UiComposeRequest ParseComposeRequest(string body)
        {
            try { return string.IsNullOrWhiteSpace(body) ? new UiComposeRequest() : JsonUtility.FromJson<UiComposeRequest>(body) ?? new UiComposeRequest(); }
            catch { return new UiComposeRequest(); }
        }

        private static UiAuditRequest ParseAuditRequest(string body)
        {
            try { return string.IsNullOrWhiteSpace(body) ? new UiAuditRequest() : JsonUtility.FromJson<UiAuditRequest>(body) ?? new UiAuditRequest(); }
            catch { return new UiAuditRequest(); }
        }

        private static bool PersistAudit(UnityAiAuditEvent auditEvent)
        {
            try
            {
                AuditLogStore.Append(new[] { auditEvent });
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError($"Failed to persist Unity AI UI audit event: {exception.Message}");
                return false;
            }
        }
    }
}
