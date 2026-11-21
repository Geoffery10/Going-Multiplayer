using System.Collections.Generic;
using Steamworks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GoingMultiplayer
{
    // Draws the Co-op tab with the game's own UI pieces: a sliced rounded menu
    // panel, the pause-menu font/sizes and cloned capsule buttons, so the tab
    // matches the surrounding menu instead of looking like a debug overlay.
    internal sealed class CoopMenuView : MonoBehaviour
    {
        private sealed class Row
        {
            internal GameObject Root;
            internal TextMeshProUGUI Name;
            internal Button Kick;
            internal TextMeshProUGUI KickLabel;
            internal ulong MemberId;
            internal string Shown;
        }

        private const float PanelWidth = 720f;
        private const float PanelHeight = 680f;
        private const int MaxRows = 6;

        private SteamCoop coop;
        private PauseMenu pause;
        private readonly List<Row> rows = new List<Row>();
        private TextMeshProUGUI header;
        private TextMeshProUGUI description;
        private TextMeshProUGUI status;
        private TextMeshProUGUI lobby;
        private TextMeshProUGUI playersLabel;
        private TextMeshProUGUI emptyLabel;
        private Button host;
        private Button join;
        private Button invite;
        private Button copy;
        private Button leave;
        private GameObject hostObject;
        private GameObject joinObject;
        private GameObject inviteObject;
        private GameObject copyObject;
        private GameObject leaveObject;
        private bool built;
        private Color textColor = Color.black;
        private Color accent = new Color(0.31f, 0.72f, 0.24f, 1f);

        internal void Initialize(SteamCoop value, PauseMenu owner, Color tabColor)
        {
            coop = value;
            pause = owner;
            accent = tabColor;
            Build();
            Refresh();
        }

        private static TextMeshProUGUI CloneText(TextMeshProUGUI source, Transform parent,
            string name, float size, TextAlignmentOptions alignment)
        {
            if (source == null) return null;
            GameObject go = Instantiate(source.gameObject, parent, false);
            go.name = name;
            foreach (MonoBehaviour behaviour in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour is TextMeshProUGUI || behaviour is Image) continue;
                Destroy(behaviour);
            }
            TextMeshProUGUI text = go.GetComponent<TextMeshProUGUI>();
            RectTransform rect = (RectTransform)go.transform;
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            text.fontSize = size;
            text.alignment = alignment;
            text.raycastTarget = false;
            text.text = "";
            return text;
        }

        private static TextMeshProUGUI FindText(Component root, float minSize, float maxSize)
        {
            foreach (TextMeshProUGUI candidate in
                root.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                if (candidate.fontSize < minSize || candidate.fontSize > maxSize) continue;
                // Menu cards use dark text; the coloured header band uses white.
                if (candidate.color.grayscale > 0.6f) continue;
                return candidate;
            }
            return null;
        }

        private static Image FindSprite(Component root, string spriteName)
        {
            foreach (Image image in root.GetComponentsInChildren<Image>(true))
            {
                if (image.sprite != null && image.sprite.name == spriteName) return image;
            }
            return null;
        }

        private Button CloneButton(Button template, Transform parent, string name,
            string label, Vector2 position, Vector2 size, float fontSize,
            UnityEngine.Events.UnityAction action)
        {
            if (template == null) return null;
            GameObject go = Instantiate(template.gameObject, parent, false);
            go.name = name;
            foreach (MonoBehaviour behaviour in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour is Button || behaviour is Image
                    || behaviour is TextMeshProUGUI) continue;
                Destroy(behaviour);
            }
            RectTransform rect = (RectTransform)go.transform;
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            Image background = go.GetComponent<Image>();
            if (background != null) background.color = accent;
            TextMeshProUGUI text = go.GetComponentInChildren<TextMeshProUGUI>(true);
            if (text != null)
            {
                text.fontSize = fontSize;
                text.alignment = TextAlignmentOptions.Center;
                text.raycastTarget = false;
                text.color = Color.white;
                text.text = label;
            }
            Button button = go.GetComponent<Button>();
            if (button != null)
            {
                button.onClick.RemoveAllListeners();
                if (action != null) button.onClick.AddListener(action);
            }
            return button;
        }

        private void Build()
        {
            if (built) return;
            built = true;

            Image panelTemplate = FindSprite(pause, "RectRoundedMenuFilled");
            Button buttonTemplate = null;
            foreach (Button candidate in pause.GetComponentsInChildren<Button>(true))
            {
                Image image = candidate.GetComponent<Image>();
                if (image != null && image.sprite != null
                    && image.sprite.name == "RectCapsule")
                {
                    buttonTemplate = candidate;
                    break;
                }
            }
            Transform parent = transform;
            TextMeshProUGUI headerSource = FindText(pause, 34f, 60f);
            if (headerSource == null) headerSource = pause.headerText;
            TextMeshProUGUI bodySource = FindText(pause, 18f, 26f);
            if (bodySource == null) bodySource = pause.descriptionText;
            if (bodySource != null) textColor = bodySource.color;

            // Panel card, same sliced sprite as the other pause-menu cards.
            GameObject panelObject = new GameObject("Coop Panel", typeof(RectTransform));
            panelObject.transform.SetParent(parent, false);
            RectTransform panelRect = (RectTransform)panelObject.transform;
            panelRect.anchorMin = panelRect.anchorMax = panelRect.pivot = new Vector2(0.5f, 0.5f);
            panelRect.sizeDelta = new Vector2(PanelWidth, PanelHeight);
            // Sit well below the coloured header band/tab strip so the card
            // never covers the tabs, even at 4:3 resolutions.
            panelRect.anchoredPosition = new Vector2(0f, -64f);
            Image panelImage = panelObject.AddComponent<Image>();
            if (panelTemplate != null)
            {
                panelImage.sprite = panelTemplate.sprite;
                panelImage.type = panelTemplate.type;
                panelImage.color = panelTemplate.color;
            }
            else
            {
                panelImage.color = Color.white;
            }
            Transform content = panelObject.transform;

            float top = PanelHeight * 0.5f;
            header = CloneText(headerSource, content, "Header", 40f,
                TextAlignmentOptions.Center);
            if (header != null)
            {
                header.text = "Co-op";
                header.rectTransform.sizeDelta = new Vector2(PanelWidth - 80f, 60f);
                header.rectTransform.anchoredPosition = new Vector2(0f, -28f);
            }

            GameObject lineObject = new GameObject("Divider", typeof(RectTransform));
            lineObject.transform.SetParent(content, false);
            RectTransform lineRect = (RectTransform)lineObject.transform;
            lineRect.anchorMin = lineRect.anchorMax = lineRect.pivot = new Vector2(0.5f, 1f);
            lineRect.sizeDelta = new Vector2(PanelWidth - 100f, 3f);
            lineRect.anchoredPosition = new Vector2(0f, -86f);
            Image lineImage = lineObject.AddComponent<Image>();
            lineImage.color = new Color(0.1f, 0.1f, 0.1f, 0.14f);
            lineImage.raycastTarget = false;

            description = CloneText(bodySource, content, "Description", 22f,
                TextAlignmentOptions.Center);
            if (description != null)
            {
                description.text = "Steam online co-op with friends";
                description.rectTransform.sizeDelta = new Vector2(PanelWidth - 80f, 34f);
                description.rectTransform.anchoredPosition = new Vector2(0f, -96f);
            }

            status = CloneText(bodySource, content, "Status", 24f,
                TextAlignmentOptions.Center);
            if (status != null)
            {
                status.rectTransform.sizeDelta = new Vector2(PanelWidth - 80f, 64f);
                status.rectTransform.anchoredPosition = new Vector2(0f, -142f);
            }

            lobby = CloneText(bodySource, content, "Lobby", 20f,
                TextAlignmentOptions.Center);
            if (lobby != null)
            {
                lobby.rectTransform.sizeDelta = new Vector2(PanelWidth - 80f, 32f);
                lobby.rectTransform.anchoredPosition = new Vector2(0f, -206f);
            }

            Vector2 buttonSize = new Vector2(210f, 54f);
            float buttonY = -250f;
            host = CloneButton(buttonTemplate, content, "Host", "Host", 
                new Vector2(-230f, buttonY), buttonSize, 20f, coop.Host);
            join = CloneButton(buttonTemplate, content, "Join", "Join clipboard",
                new Vector2(0f, buttonY), buttonSize, 20f, coop.JoinClipboard);
            invite = CloneButton(buttonTemplate, content, "Invite", "Invite",
                new Vector2(-230f, buttonY), buttonSize, 20f, coop.Invite);
            copy = CloneButton(buttonTemplate, content, "Copy", "Copy id",
                new Vector2(0f, buttonY), buttonSize, 20f, coop.CopyLobbyId);
            leave = CloneButton(buttonTemplate, content, "Leave", "Leave",
                new Vector2(230f, buttonY), buttonSize, 20f, coop.Leave);
            hostObject = host != null ? host.gameObject : null;
            joinObject = join != null ? join.gameObject : null;
            inviteObject = invite != null ? invite.gameObject : null;
            copyObject = copy != null ? copy.gameObject : null;
            leaveObject = leave != null ? leave.gameObject : null;

            playersLabel = CloneText(bodySource, content, "Players", 26f,
                TextAlignmentOptions.Left);
            if (playersLabel != null)
            {
                playersLabel.rectTransform.sizeDelta = new Vector2(PanelWidth - 80f, 34f);
                playersLabel.rectTransform.anchoredPosition = new Vector2(0f, -322f);
            }

            emptyLabel = CloneText(bodySource, content, "Empty", 22f,
                TextAlignmentOptions.Center);
            if (emptyLabel != null)
            {
                emptyLabel.text = "No Steam lobby members yet.";
                emptyLabel.rectTransform.sizeDelta = new Vector2(PanelWidth - 80f, 34f);
                emptyLabel.rectTransform.anchoredPosition = new Vector2(0f, -366f);
            }

            float rowY = -404f;
            for (int i = 0; i < MaxRows; i++)
            {
                var row = new Row();
                GameObject rowObject = new GameObject("Row " + i, typeof(RectTransform));
                rowObject.transform.SetParent(content, false);
                RectTransform rowRect = (RectTransform)rowObject.transform;
                rowRect.anchorMin = new Vector2(0.5f, 1f);
                rowRect.anchorMax = new Vector2(0.5f, 1f);
                rowRect.pivot = new Vector2(0.5f, 1f);
                rowRect.sizeDelta = new Vector2(PanelWidth - 80f, 40f);
                rowRect.anchoredPosition = new Vector2(0f, rowY - i * 44f);
                row.Root = rowObject;

                row.Name = CloneText(bodySource, rowObject.transform, "Name", 22f,
                    TextAlignmentOptions.Left);
                if (row.Name != null)
                {
                    // Line the name up with the "Players" label's left edge.
                    row.Name.rectTransform.sizeDelta = new Vector2(PanelWidth - 260f, 34f);
                    row.Name.rectTransform.anchoredPosition = new Vector2(-90f, 0f);
                }
                row.Kick = CloneButton(buttonTemplate, rowObject.transform, "Kick", "Kick",
                    new Vector2(270f, 0f), new Vector2(110f, 38f), 17f, null);
                if (row.Kick != null)
                {
                    row.KickLabel = row.Kick.GetComponentInChildren<TextMeshProUGUI>(true);
                    row.Kick.onClick.AddListener(() => coop.Kick(row.MemberId));
                }
                rowObject.SetActive(false);
                rows.Add(row);
            }
        }

        private static void SetText(TextMeshProUGUI label, string value)
        {
            if (label != null && label.text != value) label.text = value;
        }

        private void Refresh()
        {
            if (!built || coop == null) return;
            SetText(status, coop.Status);
            bool hasLobby = coop.HasLobby;
            SetText(lobby, "Lobby: " + (hasLobby ? coop.LobbyId : "-")
                + "    Role: " + (coop.IsHost ? "host" : "guest"));
            if (hostObject != null) hostObject.SetActive(!hasLobby);
            if (joinObject != null) joinObject.SetActive(!hasLobby);
            if (inviteObject != null) inviteObject.SetActive(hasLobby);
            if (copyObject != null) copyObject.SetActive(hasLobby);
            if (leaveObject != null) leaveObject.SetActive(hasLobby);

            List<SteamCoop.CoopMember> members = coop.Members;
            SetText(playersLabel, "Players (" + members.Count + ")");
            if (emptyLabel != null) emptyLabel.gameObject.SetActive(members.Count == 0);

            for (int i = 0; i < rows.Count; i++)
            {
                Row row = rows[i];
                bool show = i < members.Count;
                if (row.Root.activeSelf != show) row.Root.SetActive(show);
                if (!show) continue;
                SteamCoop.CoopMember member = members[i];
                string label = member.Name + (member.IsHost ? " (host)" : "")
                    + (member.IsMe ? " (you)" : "");
                if (row.Shown != label)
                {
                    row.Shown = label;
                    SetText(row.Name, label);
                }
                row.MemberId = member.Id;
                bool canKick = coop.IsHost && !member.IsMe && !member.IsHost;
                if (row.Kick != null && row.Kick.gameObject.activeSelf != canKick)
                    row.Kick.gameObject.SetActive(canKick);
            }
        }

        private void Update()
        {
            Refresh();
        }
    }
}
