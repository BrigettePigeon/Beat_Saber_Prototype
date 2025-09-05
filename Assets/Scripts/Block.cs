using UnityEngine;


public enum BlockColour // defines two possible colors for blocks
{
    Green, Red
}

public class Block : MonoBehaviour // controls block behavior
{
    public BlockColour colour; // this block's color

    public GameObject brokenBlockLeft; // left broken piece
    public GameObject brokenBlockRight; // right broken piece
    public float brokenBlockForce; // force to push pieces apart
    public float brokenBlockTorque; // spin force for effect
    public float brokenBlockDestroyDelay; // how long before pieces disappear

    private void OnTriggerEnter(Collider other) // called when something enters this block's trigger
    {
        if (other.CompareTag("SwordRed")) // check if red sword hit it
        {
            if (colour == BlockColour.Red) // correct color match
            {
                GameManager.instance.AddScore(); // add score
            }
            else
            {
                GameManager.instance.HitWrongBlock(); // penalize for wrong color
            }

            Hit(); // destroy the block
        }
        else if (other.CompareTag("SwordGreen")) // check if green sword hit it
        {
            if (colour == BlockColour.Green) // correct color match
            {
                GameManager.instance.AddScore(); // add score
            }
            else
            {
                GameManager.instance.HitWrongBlock(); // penalize for wrong color
            }

            Hit(); // destroy the block
        }
    }

    public void Hit() // handles breaking the block
    {
        brokenBlockLeft.SetActive(true); // show left piece
        brokenBlockRight.SetActive(true); // show right piece

        brokenBlockLeft.transform.parent = null; // unparent left piece
        brokenBlockRight.transform.parent = null; // unparent right piece

        Rigidbody leftRig = brokenBlockLeft.GetComponent<Rigidbody>(); // get left physics body
        Rigidbody rightRig = brokenBlockRight.GetComponent<Rigidbody>(); // get right physics body

        leftRig.AddForce(-transform.right * brokenBlockForce, ForceMode.Impulse); // push left piece left
        rightRig.AddForce(transform.right * brokenBlockForce, ForceMode.Impulse); // push right piece right

        leftRig.AddTorque(-transform.forward * brokenBlockTorque, ForceMode.Impulse); // spin left piece
        rightRig.AddTorque(transform.forward * brokenBlockTorque, ForceMode.Impulse); // spin right piece

        Destroy(brokenBlockLeft, brokenBlockDestroyDelay); // remove left piece after delay
        Destroy(brokenBlockRight, brokenBlockDestroyDelay); // remove right piece after delay

        Destroy(gameObject); // destroy main block
    }
}