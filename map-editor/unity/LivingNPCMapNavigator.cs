using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.UI;

namespace LivingNPC.Maps
{
    /// <summary>
    /// Minimal Unity viewer for the format exported by LivingNPC Map Editor.
    /// Requires the com.unity.nuget.newtonsoft-json package.
    /// Copy the contents of the ZIP into Assets/Resources/Maps/MapName
    /// and set mapRoot, for example: Maps/MapName.
    /// </summary>
    public sealed class LivingNPCMapNavigator : MonoBehaviour
    {
        [Header("Progetto")]
        [SerializeField] private string mapRoot = "Maps/Aurea";
        [SerializeField] private string variant = "day";

        [Header("Interfaccia")]
        [SerializeField] private Image backgroundImage;
        [SerializeField] private Text titleText;
        [SerializeField] private Text narrativeText;
        [SerializeField] private RectTransform hotspotLayer;
        [SerializeField] private Button hotspotPrefab;

        private readonly Dictionary<string, LivingNPCMapNode> nodes = new();
        private LivingNPCMapProject project;
        private string currentNodeId;

        private void Start()
        {
            LoadProject();
            ShowNode(project.startNode);
        }

        public void SetVariant(string newVariant)
        {
            variant = newVariant;
            if (!string.IsNullOrEmpty(currentNodeId)) ShowNode(currentNodeId);
        }

        public void LoadProject()
        {
            nodes.Clear();
            TextAsset manifestAsset = Resources.Load<TextAsset>($"{mapRoot}/project");
            if (manifestAsset == null)
                throw new MissingReferenceException($"Missing Resources/{mapRoot}/project.json");

            project = JsonConvert.DeserializeObject<LivingNPCMapProject>(manifestAsset.text);
            foreach (LivingNPCMapProjectNode entry in project.nodes)
            {
                TextAsset nodeAsset = Resources.Load<TextAsset>($"{mapRoot}/nodes/{entry.id}/node");
                if (nodeAsset == null)
                {
                    Debug.LogError($"Missing Resources/{mapRoot}/nodes/{entry.id}/node.json");
                    continue;
                }
                LivingNPCMapNode node = JsonConvert.DeserializeObject<LivingNPCMapNode>(nodeAsset.text);
                nodes[node.id] = node;
            }
        }

        public void ShowNode(string nodeId)
        {
            if (!nodes.TryGetValue(nodeId, out LivingNPCMapNode node))
            {
                Debug.LogError($"Unknown LivingNPC node: {nodeId}");
                return;
            }

            currentNodeId = nodeId;
            titleText.text = node.title;
            narrativeText.text = node.text;
            string backgroundVariant = node.backgrounds.ContainsKey(variant) ? variant : "day";
            string filename = node.backgrounds[backgroundVariant];
            string pathWithoutExtension = System.IO.Path.ChangeExtension(filename, null);
            Texture2D texture = Resources.Load<Texture2D>($"{mapRoot}/nodes/{node.id}/{pathWithoutExtension}");
            if (texture != null)
            {
                backgroundImage.sprite = Sprite.Create(
                    texture,
                    new Rect(0, 0, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f),
                    100f);
                backgroundImage.preserveAspect = true;
            }

            for (int i = hotspotLayer.childCount - 1; i >= 0; i--)
                Destroy(hotspotLayer.GetChild(i).gameObject);

            Canvas.ForceUpdateCanvases();
            foreach (LivingNPCMapLink link in node.links)
            {
                Button button = Instantiate(hotspotPrefab, hotspotLayer);
                button.name = link.id;
                RectTransform rect = button.GetComponent<RectTransform>();
                rect.anchorMin = rect.anchorMax = new Vector2(link.x / 100f, 1f - link.y / 100f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.anchoredPosition = Vector2.zero;
                rect.sizeDelta = new Vector2(
                    hotspotLayer.rect.width * link.width / 100f,
                    hotspotLayer.rect.height * link.height / 100f);

                Text label = button.GetComponentInChildren<Text>();
                if (label != null) label.text = link.label;
                string target = link.target;
                button.onClick.AddListener(() => ShowNode(target));
            }
        }
    }
}
