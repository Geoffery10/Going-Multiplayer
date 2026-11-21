using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace GoingMultiplayer
{
    // Adds one tab to the game's ESC/pause menu. The tab content is IMGUI so it
    // does not depend on authored UI prefabs; the tab button is cloned from the
    // existing pause menu so it matches the surrounding control.
    internal sealed class CoopPauseTab : MonoBehaviour
    {
        private SteamCoop coop;
        private PauseMenu pause;
        private MenuTab menuTab;
        private static PauseMenu installedOn;

        internal void Initialize(SteamCoop value, PauseMenu owner, MenuTab tab)
        {
            coop = value;
            pause = owner;
            menuTab = tab;
        }

        internal static string Install(PauseMenu owner, SteamCoop coop, out bool ok)
        {
            ok = false;
            if (owner == null || owner.tabs == null || owner.tabs.Count == 0)
                return "no pause menu";
            if (installedOn == owner) { ok = true; return "already installed"; }
            FieldInfo field = typeof(PauseMenu).GetField("tabButtons",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var ownerButtons = field != null
                ? field.GetValue(owner) as List<PauseTabButton> : null;
            if (ownerButtons == null || ownerButtons.Count == 0) return "no tab buttons";

            MenuTab source = owner.tabs[0];
            PauseTabButton sourceButton = ownerButtons[ownerButtons.Count - 1];
            PauseTabButton iconSource = ownerButtons.Count > 1
                ? ownerButtons[1] : sourceButton;
            if (source == null || sourceButton == null) return "no source tab";

            // Build a bare tab: no cloned content, so nothing can draw over the
            // IMGUI panel and no authored prefab references are required.
            GameObject tabObject = new GameObject("GMP Co-op Tab", typeof(RectTransform));
            tabObject.transform.SetParent(source.transform.parent, false);
            RectTransform sourceRect = source.GetComponent<RectTransform>();
            RectTransform tabRect = (RectTransform)tabObject.transform;
            if (sourceRect != null)
            {
                tabRect.anchoredPosition = sourceRect.anchoredPosition;
                tabRect.sizeDelta = sourceRect.sizeDelta;
                tabRect.anchorMin = sourceRect.anchorMin;
                tabRect.anchorMax = sourceRect.anchorMax;
                tabRect.pivot = sourceRect.pivot;
            }
            MenuTab tab = tabObject.AddComponent<MenuTab>();
            tab.titleIndex = "COOP";
            tab.descriptionIndex = "COOP";
            tab.color1 = source.color1;
            tab.backgroundSprite = source.backgroundSprite;
            tab.autoInitSelectables = true;
            tab.orientation = MenuWindow.Orientation.Horizontal;
            tabObject.AddComponent<CoopPauseTab>()
                .Initialize(coop, owner, tab);
            tabObject.AddComponent<CoopMenuView>().Initialize(coop, owner, tab.color1);
            tabObject.SetActive(false);

            PauseTabButton button = UnityEngine.Object.Instantiate(
                iconSource != null ? iconSource : sourceButton,
                sourceButton.transform.parent);
            button.gameObject.name = "GMP Co-op Tab Button";
            button.gameObject.SetActive(true);
            button.tabName = "Coop";
            Button click = button.button != null ? button.button : button.GetComponent<Button>();
            if (click != null)
            {
                click.onClick.RemoveAllListeners();
                click.onClick.AddListener(tab.OnTabClicked);
            }
            RectTransform sourceButtonRect = sourceButton.GetComponent<RectTransform>();
            RectTransform buttonRect = button.GetComponent<RectTransform>();
            if (sourceButtonRect != null && buttonRect != null)
            {
                float step = 89.4f;
                if (ownerButtons.Count > 1)
                {
                    RectTransform prev = ownerButtons[ownerButtons.Count - 2]
                        .GetComponent<RectTransform>();
                    if (prev != null)
                    {
                        float delta = sourceButtonRect.anchoredPosition.x
                            - prev.anchoredPosition.x;
                        if (Mathf.Abs(delta) > 1f) step = delta;
                    }
                }
                buttonRect.anchoredPosition = sourceButtonRect.anchoredPosition
                    + new Vector2(step, 0f);
            }

            owner.tabs.Add(tab);
            ownerButtons.Add(button);
            Transform parent = sourceButton.transform.parent;
            RectTransform parentRect = parent as RectTransform;
            if (parentRect != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(parentRect);
            installedOn = owner;
            ok = true;

            string detail = " tabs=" + owner.tabs.Count
                + " buttons=" + ownerButtons.Count
                + " sourceTab=" + source.name
                + " sourceButton=" + sourceButton.name
                + " parent=" + (parent != null ? parent.name : "none");
            for (int i = 0; i < ownerButtons.Count; i++)
            {
                RectTransform rect = ownerButtons[i] != null
                    ? ownerButtons[i].GetComponent<RectTransform>() : null;
                detail += " b" + i + "=" + (ownerButtons[i] != null
                    ? ownerButtons[i].gameObject.name : "null")
                    + (rect != null ? "@" + rect.anchoredPosition : "");
            }
            return detail;
        }


        internal static void Dump(Transform root, System.Action<string> report, int limit)
        {
            int count = 0;
            DumpNode(root, 0, report, ref count, limit);
        }

        private static void DumpNode(Transform node, int depth,
            System.Action<string> report, ref int count, int limit)
        {
            if (node == null || count >= limit) return;
            count++;
            var sb = new StringBuilder("GMP_UI ");
            sb.Append(new string('.', depth * 2));
            sb.Append(node.name);
            RectTransform rect = node as RectTransform;
            if (rect != null)
                sb.Append(" [").Append(rect.anchoredPosition.x.ToString("0"))
                  .Append(',').Append(rect.anchoredPosition.y.ToString("0"))
                  .Append(" ").Append(rect.sizeDelta.x.ToString("0"))
                  .Append('x').Append(rect.sizeDelta.y.ToString("0")).Append(']');
            Image image = node.GetComponent<Image>();
            if (image != null)
                sb.Append(" Image(").Append(image.sprite != null ? image.sprite.name : "none")
                  .Append(image.raycastTarget ? "" : " noray").Append(')');
            TMPro.TextMeshProUGUI tmp = node.GetComponent<TMPro.TextMeshProUGUI>();
            if (tmp != null)
            {
                string text = tmp.text != null && tmp.text.Length > 26
                    ? tmp.text.Substring(0, 26) : (tmp.text ?? "");
                sb.Append(" TMP(\"").Append(text.Replace('\n', ' ')).Append("\" size=")
                  .Append(tmp.fontSize.ToString("0")).Append(')');
            }
            LocalizedText localized = node.GetComponent<LocalizedText>();
            if (localized != null)
                sb.Append(" Loc(").Append(localized.index).Append(')');
            Button button = node.GetComponent<Button>();
            if (button != null) sb.Append(" Button");
            Selectable selectable = node.GetComponent<Selectable>();
            if (selectable != null && button == null) sb.Append(" Selectable");
            report(sb.ToString());
            for (int i = 0; i < node.childCount; i++)
                DumpNode(node.GetChild(i), depth + 1, report, ref count, limit);
        }

        private void LateUpdate()
        {
            if (pause == null || menuTab == null) return;
            if (!menuTab.gameObject.activeInHierarchy) return;
            if (pause.headerText != null) pause.headerText.text = "Co-op";
            if (pause.descriptionText != null)
                pause.descriptionText.text = "Play together online.";
            if (pause.tabPromptText != null) pause.tabPromptText.text = "Co-op";
        }

    }
}
