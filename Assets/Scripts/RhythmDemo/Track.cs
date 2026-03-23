using UnityEngine;
using UnityEngine.Rendering;

public class Track : MonoBehaviour
{
    public SongData song; // data asset with song 
    public AudioSource audioSource; // plays the clip

    private void Start() 
    {
        transform.position = Vector3.forward * (song.speed * GameManager.instance.startTime); // move track forward to sync with start delay
        Invoke("StartSong", GameManager.instance.startTime - song.startTime); // delay music to match block travel time
    }

    void StartSong() // plays song after delay
    {
        audioSource.PlayOneShot(song.song); // start song playback
        Invoke("SongIsOver", song.song.length); // schedule win when song ends
    }

    private void Update() 
    {
        transform.position += Vector3.back * song.speed * Time.deltaTime; // move track backwards to simulate blocks moving
    }

    void SongIsOver() // when song ends
    {
        GameManager.instance.WinGame(); // call win state
    }

    private void OnDrawGizmos() // draws helper lines in editor
    {
        for (int i = 0; i < 100; i++) // loop through 100 beats
        {
            float beatLength = 60.0f / (float)song.bpm; // time per beat
            float beatDist = beatLength * song.speed; // distance per beat

            Gizmos.DrawLine( // draw line in scene view
                transform.position + new Vector3(-1, 0, i * beatDist), // start point (left)
                transform.position + new Vector3(1, 0, i * beatDist)   // end point (right)
            );
        }
    }


}
