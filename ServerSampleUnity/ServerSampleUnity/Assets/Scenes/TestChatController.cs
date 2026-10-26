using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class TestChatController : MonoBehaviour
{
    public InputField inputField;
    public Button btn;
    private void Awake()
    {
        btn.onClick.AddListener(() =>
        {
            SocketController.Instance.SendText(MessageType.Chat, inputField.text);
            inputField.text = string.Empty;
        });
    }
    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
