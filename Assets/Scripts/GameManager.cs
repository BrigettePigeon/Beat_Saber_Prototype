using UnityEngine;
using System.Collections.Generic;
using System.Collections;
using UnityEngine.SceneManagement;


public class GameManager : MonoBehaviour 
{
    public GameObject WinCanvas; // shown when player wins
    public GameObject LoseCanvas; // shown when player loses
    public GameObject UICanvas; // main gameplay UI

    public float startTime = 3.0f; // delay before song starts
    public int score; // current player score
    public float lifeTime = 1.0f; // player health (0 to 1)
    public int hitBlockScore = 10; // points per correct hit
    public float missBlockLife = 0.1f; // life lost for missed block
    public float wrongBlockLife = 0.08f; // life lost for wrong color
    public float lifeRegenRate = 0.1f; // slow regen over time

    // public float swordHitVelocityThreshold = 0.5f; // (commented out) velocity threshold for scoring

    public VelocityTracker leftSwordTracker; // used to check green sword speed
    public VelocityTracker rightSwordTracker; // used to check red sword speed

    public static GameManager instance; // global access point

    private void Awake() 
    {
        instance = this; // set global reference
    }

    public void AddScore() // called on successful block hit
    {
        score += hitBlockScore; // increase score
        GameUI.instance.UpdateScoreText(); // update UI text
    }

    public void MissBlock() // called on missed block
    {
        lifeTime -= missBlockLife; // reduce life
    }

    public void HitWrongBlock() // called on wrong color hit
    {
        lifeTime -= wrongBlockLife; // reduce life
    }

    private void Update() 
    {
        lifeTime = Mathf.MoveTowards(lifeTime, 1.0f, lifeRegenRate * Time.deltaTime); // regenerate life slowly
        if (lifeTime <= 0.0f) // check if health is gone
            LoseGame(); // trigger lose state

        GameUI.instance.UpdateLifetimeBar(); // update health UI
    }

    public void WinGame() // when player completes song
    {
        UICanvas.SetActive(false); // hide gameplay UI
        WinCanvas.SetActive(true); // show win screen
    }

    public void LoseGame() // when player runs out of life
    {
        UICanvas.SetActive(false); // hide gameplay UI
        LoseCanvas.SetActive(true); // show lose screen
    }
}