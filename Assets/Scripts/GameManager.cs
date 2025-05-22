using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class GameManager : MonoBehaviour
{
    public TMP_Text[] clueTexts; // Clue metinleri için TextMeshPro array
    private bool[] clueRevealed; // Hangi clue'lar gösterildi, kontrol için

    public Button[] actionButtons; // 3 buttons
    public TMP_Text[] buttonTexts; // TMP versions of button labels
    public TMP_Text outputText;

    private string[,] actions = new string[3, 3]
    {
        { "Examine the bullet casing for fingerprints or DNA.", 
            "Confront the ex-employee about why he kept the gun and whether he had any reason to want revenge against Mr. Lennox.", 
            "Ask Mrs. Lennox if she ever saw the painting herself." },
        { "Search the safe for any hidden compartments or documents.", 
            "Compare the receipt to other purchases made around that time to see if anyone else bought anything similar.", 
            "Ask Miss Bray why she purchased the painting and whether she knows anything about its history." },
        { "Interview Miss Bray about her recent interactions with the victim.", 
            "Confront Miss Bray about why she didn’t tell anyone else about the painting and whether she knew anything about its previous owner or where it came from.", 
            "Search the law firm’s trash bins for any shredded documents related to the painting or Miss Bray’s involvement." }
    };
    
    // Her action'a karşılık gelen özel açıklama
    private string[,] actionOutputs = new string[3, 3]
    {
        {
            "The bullet casing was matched to a gun owned by a former employee of the Lennox estate who had been fired six months ago. " +
            "His prints were still on file.",
            "The ex-employee, Mr. Rex Mills, denied ever owning the gun or having any motive to harm Mr. Lennox. He said he had only kept it as a souvenir from his time working at the estate.\n\nHowever, when asked about the painting, he became visibly upset and said he had seen it disappear shortly after it arrived at the house. He claimed he had told Mr. Lennox about it but was ignored.\n",
            "Mrs. Lennox confirmed that she had seen the painting once, but it had been taken down soon after she mentioned it to her husband. She said she didn’t know where it went or who might have taken it.\n\nWhen asked about the note left near the body, she said she hadn’t written it and didn’t recognize the handwriting. However, she did admit that she had argued with her husband over the painting earlier that day and had accused him of hiding something important.\n"
        },
        {
            "The safe was opened using a key that wasn’t on the ring given to Mrs. Lennox. Inside, there was a small envelope labeled ‘For Vincent Only.’ " +
            "Inside was a receipt dated three days ago for a purchase made under a fake name.",
            "The receipt matches another purchase made at the same store under a different name — Miss Tessa Bray, the family lawyer.",
            "Miss Bray admitted to purchasing the painting but said she had done so on behalf of Mr. Lennox as part of a secret deal. She said he had wanted to keep it hidden from his wife because he feared she would sell it for profit."
        },
        {
            "Miss Bray confirmed that she had met with Mr. Lennox several times over the past week to discuss the painting and how best to hide it from his wife.\nShe said he had become increasingly paranoid about someone finding out about the purchase and had asked her to keep it quiet until he could figure out what to do with it.\n",
            "Miss Bray admitted that she had been hired by a third party to steal the painting and plant evidence to make it look like Mr. Lennox had killed himself.\nShe said she had been paid to keep the painting hidden and had been instructed to destroy any records linking her to the transaction.\n",
            "The trash bin behind Miss Bray’s office contained a torn piece of paper with a partial address written in pencil. When compared to the receipt found in the safe, it matched the address listed as the buyer’s contact information."
        }
    };

    private string[] suspects = { "Mrs. Lennox, the victim's wife", "Miss Tessa Bray, the family lawyer", "Mr. Jasper Hale, a rival art dealer" };
    private string correctSuspect = "Miss Tessa Bray, the family lawyer";

    private int[] actionIndices = new int[3]; // to track each button's progress
    private bool suspectsPhase = false;

    void Start()
    {
        outputText.text = "What will you do?";
        for (int i = 0; i < actionButtons.Length; i++)
        {
            int index = i;
            buttonTexts[i].text = actions[i, 0];
            actionButtons[i].onClick.AddListener(() => OnActionClicked(index));
        }
        clueRevealed = new bool[clueTexts.Length];
        for (int i = 0; i < clueTexts.Length; i++)
        {
            clueTexts[i].text = ""; // Başta boş olsun
        }

    }

    void OnActionClicked(int buttonIndex)
    {
        if (!suspectsPhase)
        {
            int step = actionIndices[buttonIndex];
            outputText.text = actionOutputs[buttonIndex, step];

            
            // 1. butonun 1. action'u seçildiyse
            if (buttonIndex == 0 && step == 0 && !clueRevealed[0])
            {
                clueTexts[0].text = "The ex-employee admitted to keeping the gun because he believed Mr. Lennox had stolen something valuable from him during a recent renovation project. " +
                                    "He said he had seen the victim handling the painting before it disappeared.";
                clueRevealed[0] = true;
            }

            // 1. butonun 2. adımı
            if (buttonIndex == 0 && step == 1 && !clueRevealed[1])
            {
                clueTexts[1].text = "The painting may have been hidden somewhere else in the house.";
                clueRevealed[1] = true;
            }
            
            // 2. butonun 2. adımı
            if (buttonIndex == 1 && step == 1 && !clueRevealed[1])
            {
                clueTexts[2].text = "Miss Bray may have been involved in the theft of the painting.";
                clueRevealed[2] = true;
            }

            if (step < 2)
            {
                actionIndices[buttonIndex]++;
                buttonTexts[buttonIndex].text = actions[buttonIndex, actionIndices[buttonIndex]];
            }

            if (actionIndices[0] >= 2 && actionIndices[1] >= 2 && actionIndices[2] >= 2)
            {
                suspectsPhase = true;
                outputText.text = "Who is the criminal?";
                for (int i = 0; i < 3; i++)
                {
                    buttonTexts[i].text = suspects[i];
                }
            }
        }
        else
        {
            string chosenSuspect = buttonTexts[buttonIndex].text;
            if (chosenSuspect == correctSuspect)
                outputText.text = "You solved the case! " +
                                  "Real Murderer: Miss Tessa Bray, the family lawyer.\n\nMotive: To steal the painting and frame Mr. Lennox for its disappearance.\n\nDecisive Proof:\n- The receipt found in the safe matches one purchased by Miss Bray under a false name.\n- Miss Bray admitted to being hired by a third party to steal the painting and plant evidence to make it look like self-defense.\n- The torn piece of paper found in the trash bin matches the address listed on the receipt.\n";
            else
                outputText.text = "That's not the criminal. Try again.";
        }
    }
}
