using UnityEngine;
using UnityEngine.UIElements;

public sealed class NetStatusLabel : MonoBehaviour
{
    public UIDocument Document;
    public string LabelName = "net-status-label";

    private Label _label;
    private NetClient _client;
    private string _lastText;

    private UdpClientConnect _driver;

    private void Awake()
    {
        if (Document == null)
        {
            Debug.LogError("NetStatusLabel: UIDocument not assigned.");
            enabled = false;
            return;
        }

        _label = Document.rootVisualElement.Q<Label>(LabelName);
        if (_label == null)
        {
            Debug.LogError($"NetStatusLabel: Label '{LabelName}' not found.");
            enabled = false;
            return;
        }

        _driver = FindFirstObjectByType<UdpClientConnect>();
        if (_driver == null)
        {
            Debug.LogError("NetStatusLabel: UdpClientConnect not found.");
            enabled = false;
            return;
        }
    }

    private void Update()
    {
        if (_client == null)
        {
            _client = _driver.Client;
            if (_client == null)
                return;
        }

        string text = _client.StatusText;
        if (text == _lastText)
            return;

        _lastText = text;
        _label.text = text;
        _label.style.color = _client.IsTerminal
            ? new Color(1f, 0.4f, 0.4f)
            : Color.white;
    }
}
