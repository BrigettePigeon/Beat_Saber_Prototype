using UnityEngine;

public class VelocityTracker : MonoBehaviour // tracks speed of object
{
    public Transform tracker; // optional external transform to track
    public Vector3 velocity; // current velocity

    private Vector3 lastFramePos; // track position in previous frame

    private void Update() 
    {
        velocity = (transform.position - lastFramePos) / Time.deltaTime; // calculate velocity
        lastFramePos = transform.position; // update last position
    }
}