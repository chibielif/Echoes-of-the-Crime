using TMPro;
using UnityEngine;

// Cycles a footer line of detective-fiction trivia while the player waits on the
// Loading scene, so a 1-3 min. wait has something to read instead of a static screen.
// Starts at a random fact, then walks the list in order (wrapping around) so nothing
// repeats until every fact has been shown once.
public class LoadingFactsRotator : MonoBehaviour
{
    [SerializeField] private TMP_Text factText;
    [SerializeField] private float intervalSeconds = 16f;

    private static readonly string[] Facts =
    {
        "Edgar Allan Poe's \"The Murders in the Rue Morgue\" (1841) is widely considered the first modern detective story.",
        "Poe's detective C. Auguste Dupin inspired later icons like Sherlock Holmes and Hercule Poirot.",
        "Sherlock Holmes never actually says \"Elementary, my dear Watson\" in any of Arthur Conan Doyle's original stories.",
        "Conan Doyle tried to kill off Sherlock Holmes at the Reichenbach Falls in 1893 - public outcry brought him back a decade later.",
        "Agatha Christie is the best-selling novelist of all time, with an estimated two billion copies sold worldwide.",
        "Agatha Christie vanished for eleven days in 1926, setting off a real, headline-making mystery of her own.",
        "Hercule Poirot is the only fictional character ever given an obituary on the front page of The New York Times.",
        "Wilkie Collins' \"The Moonstone\" (1868) is often cited as the first full-length detective novel in English.",
        "The word \"detective\" entered English usage in the 1840s, around the same time detective fiction itself was born.",
        "A \"locked-room mystery\" is a case where the crime seems impossible because the scene was sealed from the inside.",
        "The term \"whodunit\" first appeared in print in 1930.",
        "Dashiell Hammett, author of \"The Maltese Falcon,\" worked as a real Pinkerton detective before he became a writer.",
        "Raymond Chandler's Philip Marlowe helped define the cynical, hardboiled detective archetype in American fiction.",
        "G.K. Chesterton's Father Brown solves crimes through psychology and intuition rather than physical evidence.",
        "The Baker Street Irregulars, a band of street children, gathered information for Sherlock Holmes in several stories.",
        "\"Ellery Queen\" was both a fictional detective and the shared pen name of cousins Frederic Dannay and Manfred Lee.",
        "Dorothy L. Sayers, creator of Lord Peter Wimsey, was among the first women ever awarded a degree by Oxford University.",
        "Umberto Eco's \"The Name of the Rose\" wraps a medieval murder mystery around a deep streak of philosophy and semiotics.",
    };

    private int index;

    void Start()
    {
        if (factText == null || Facts.Length == 0)
            return;

        index = Random.Range(0, Facts.Length);
        factText.text = Facts[index];
        InvokeRepeating(nameof(ShowNextFact), intervalSeconds, intervalSeconds);
    }

    private void ShowNextFact()
    {
        index = (index + 1) % Facts.Length;
        factText.text = Facts[index];
    }
}
