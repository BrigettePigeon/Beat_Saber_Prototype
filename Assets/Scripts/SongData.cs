using UnityEngine;
using Unity.Collections;

[CreateAssetMenuAttribute(fileName = "Song Data", menuName = "New Song Data")] // enables creation in editor
public class SongData : ScriptableObject // defines a data container 
{
    public AudioClip song; // audio file for the track
    public int bpm; // beats per minute
    public float startTime; // when song should actually start
    public float speed; // how fast blocks move (units per second)
}
