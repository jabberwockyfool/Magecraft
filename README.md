<p align="center">
  <img src="ICON.ico" alt="Magecraft" width="96">
</p>

# Magecraft

Outillage Windows pour traduire les visual novels MAGES. basés sur le moteur SC3
(CHAOS;CHILD et apparentés). Il s'appuie sur
[sc3tools](https://github.com/CommitteeOfZero/sc3tools) du Committee of Zero pour la
conversion `.scx` ↔ texte, et embarque son propre lecteur/écrivain MPK (même format que
[SGOMPK](https://github.com/mrboms/SGOMPK) et `mpkpack.py`) pour que la construction de
l'archive se fasse sans étape manuelle.

Deux programmes, selon le besoin :

| | |
|---|---|
| **Magecraft** | Vérifie les scripts traduits d'un dépôt, puis reconstruit l'archive en un clic. |
| **Magecraft Packer** | Le strict nécessaire : un dossier de `.scx`, un dossier de `.txt`, un `.mpk` en sortie. |

## Magecraft

Double-cliquer `Magecraft.exe`, puis **Changer…** pour désigner la racine du dépôt de
traduction — le dossier qui contient `script-decompile` (les textes) et `script-compile`
(les `.scx`). Le choix est mémorisé.

| Bouton | Effet |
|---|---|
| **Vérifier** | Analyse les scripts, liste les problèmes et ce qui reste à compiler. |
| **Corriger les caractères** | Applique les corrections sûres directement dans `script-decompile`. |
| **Construire** | `script-decompile` → `script-compile\*.scx` → `languagebarrier\enscript.mpk`. |

Options de construction : **Tout recompiler** (sinon seuls les scripts dont le texte est
plus récent que le `.scx` sont retraités), **Reconstruire enscript.mpk**, et **Installer
dans le jeu** pour copier l'archive et tester tout de suite. L'ancienne archive est
conservée en `.mpk.bak`.

### La référence

Pour contrôler la structure, Magecraft relit le texte réellement contenu dans les `.scx`
de `script-compile` avec `sc3tools extract-text` : c'est la structure des scripts du jeu,
pas un export qui peut avoir vieilli.

Conséquence à connaître : comme les `.scx` ont été compilés à partir de ces mêmes textes,
une balise perdue à la traduction est déjà perdue dans le script compilé — la comparaison
ne peut donc plus la signaler. Ce contrôle-là demanderait une référence dans la langue
d'origine.

### Ce qui est vérifié

- **Parité de lignes** avec le script compilé — un décalage désynchronise tout le fichier.
- **Caractères refusés par le jeu.** Dans CHAOS;CHILD, `œ Œ « » … Ÿ €` et le guillemet
  droit `"` n'existent pas dans le charset : sc3tools refuse alors **le fichier entier**.
  En revanche `à â ä ç é è ê ë î ï ô ö ù û ü ÿ` et les guillemets courbes `“ ” ‘ ’` passent.
  Corrections proposées : `œ → oe`, `« » → “ ”`, `… → ...`.
- **Retours chariot isolés.** Un `\r` sans `\n` — invisible dans un éditeur — est compté
  comme un caractère de texte et bloque la compilation du fichier entier.
- **Balises.** Erreur si une balise structurante disparaît (`[%p]`, `[%e]`, `[name]`,
  `[line]`, `[evaluate]` — c'est du code, pas du texte) ; avertissement pour la mise en
  forme (`[color]`, `[linebreak]`, `[margin]`), que la traduction peut légitimement
  redistribuer.
- **Crochets déséquilibrés**, comparés au script compilé pour ne pas signaler les tables
  de ponctuation qui en contiennent déjà.
- **Lignes à compiler** : le texte diffère du `.scx` en place, donc la prochaine
  construction le changera.

### Détection du charset

La table de caractères est bien embarquée dans `sc3tools.exe`, mais mélangée à celle des
autres jeux : `…` y figure alors que CHAOS;CHILD le refuse. Magecraft interroge donc
sc3tools lui-même, une fois par caractère, et met les verdicts en cache dans
`charset-cache.txt` à côté de l'exécutable. Supprimer ce fichier force une nouvelle
détection, utile après une mise à jour de sc3tools.

## Magecraft Packer

Quatre champs, un bouton : dossier des `.scx`, dossier des `.txt`, archive `.mpk` à créer,
et `sc3tools.exe`. Les chemins sont mémorisés.

Un `.txt` est associé à son script par le nom : `cc_01_01.scx` accepte
`cc_01_01.scx.txt` comme `cc_01_01.txt`. Un `.scx` sans texte correspondant est recopié
tel quel. Les `.scx` sources ne sont jamais modifiés — la compilation se fait sur des
copies temporaires.

Par défaut, un seul échec de compilation annule la création de l'archive, pour ne pas
livrer un patch amputé sans s'en rendre compte ; une case permet de forcer.

**Ordre des entrées.** Le jeu adresse les scripts par identifiant, pas par nom. Les
fichiers sont donc triés par nom et numérotés `0..n-1`, ce qui reproduit exactement la
table de `enscript.mpk`. Il faut donc fournir **tous** les scripts de l'archive, pas
seulement ceux qui ont changé, sinon les identifiants se décalent.

## Compilation

```powershell
dotnet build -c Release
```

Sorties : `src\Magecraft\bin\Release\Magecraft.exe` et
`src\Magecraft.Packer\bin\Release\MagecraftPacker.exe`.

Cible .NET Framework 4.8, livré d'origine avec Windows 10/11 : rien à installer pour les
traducteurs. Le SDK dotnet suffit à compiler, Visual Studio n'est pas nécessaire.

## Crédits

- **Committee of Zero** pour `sc3tools` et le travail de rétro-ingénierie sur le moteur SC3.
- **mrboms** pour SGOMPK, qui a servi de référence sur le format MPK.
- **MAGES. Inc.** pour les jeux d'origine.

Outil amateur, sans affiliation avec MAGES.
