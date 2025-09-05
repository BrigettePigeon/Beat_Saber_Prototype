using UnityEngine;

public class MissHitDetector : MonoBehaviour
{
    private void OnTriggerEnter(Collider other)
    {
        if(other.CompareTag("Block")) //check for block tag
        {
            other.GetComponent<Block>().Hit(); //run hit function on block
            GameManager.instance.MissBlock(); //count it as missed block (tell game manager) 
        }
    }

    
}
