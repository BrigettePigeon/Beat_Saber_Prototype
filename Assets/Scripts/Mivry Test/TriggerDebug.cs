using UnityEngine;


public class TriggerPrinter : MonoBehaviour
{
    // Must be public, non-static, and parameterless
    public void PrintTriggerPressed()
    {
        Debug.Log("trigger pressed");
    }
}