using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class GameUI : MonoBehaviour 
{
    public TextMeshProUGUI scoreText; // reference to score display text
    public Image lifetimeBar; // reference to health bar image

    public static GameUI instance; // global access to this script

    void Awake() 
    {
        instance = this; // set global reference
    }

    public void UpdateScoreText() // update score UI
    {
        scoreText.text = string.Format("SCORE\n{0}", GameManager.instance.score.ToString()); // display current score
    }

    public void UpdateLifetimeBar() // update health bar UI
    {
        lifetimeBar.fillAmount = GameManager.instance.lifeTime; // set fill amount to match life
    }
}