using UnityEngine;

public class GestureStart : MonoBehaviour
{
    public GameObject GameStart;
    public GameObject GreenSword;
    public GameObject RedSword;

    public void OnGestureCompleted(GestureCompletionData gestureCompletionData)
    {
        if (gestureCompletionData.gestureID < 0)
        {
            //failure
            string errorMessage = GestureRecognition.getErrorMessage(gestureCompletionData.gestureID);
            return;
        }

        if (gestureCompletionData.similarity >= 0.3f)
        {
            // success
            GameStart.SetActive(true);
            GreenSword.SetActive(true);
            RedSword.SetActive(true);
        }
    }
}