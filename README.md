# RoueConvert

**Ctrl + clic gauche** sur un fichier (Explorateur ou Bureau) : une roue s'ouvre autour du curseur,
tu choisis un format et une copie convertie est créée à côté de l'original (`photo.png` → `photo.jpg`,
`photo (1).jpg` si le nom existe déjà). L'original n'est jamais modifié.

## Lancer

`bin\RoueConvert.exe` — une icône (roue colorée) apparaît dans la barre des tâches, à côté de l'horloge.
Clic droit dessus : activer/désactiver, **lancer au démarrage de Windows**, quitter.

Dans la roue : clic sur un format, ou touches `1`–`9`. `Échap`, clic droit ou clic à côté pour fermer.

## Conversions

| Source | Cibles |
|---|---|
| png jpg bmp tiff ico webp | png jpg webp bmp gif tiff ico pdf |
| gif | png jpg webp bmp tiff pdf mp4 webm |
| heic avif | jpg png webp bmp tiff pdf |
| svg | png jpg pdf ico |
| pdf | png jpg (une image par page) |
| mp3 wav flac ogg m4a opus aac wma aiff | mp3 wav flac ogg m4a opus |
| mp4 mkv webm mov avi wmv flv m4v mpg ts 3gp | mp4 mkv webm mov avi gif mp3 wav |
| txt md html docx rtf | entre eux + pdf |
| csv tsv xlsx | csv tsv xlsx json |
| json / xml | csv xml yaml / json |

Audio, vidéo et webp utilisent **ffmpeg** (trouvé dans le PATH / winget). Les PDF de texte et les SVG
utilisent **Microsoft Edge** en mode invisible. Les PDF → images utilisent le moteur PDF intégré à Windows.

## Compiler

```powershell
.\build.ps1
```

Utilise le compilateur C# fourni avec Windows (.NET Framework 4.8) : rien à installer.
`bin\RoueConvert.Cli.exe convert <fichier> <ext>` permet de tester une conversion en ligne de commande.

## Limites

- Ctrl + clic sur un fichier convertible n'ajoute plus le fichier à la sélection dans l'Explorateur
  (sur un dossier, un raccourci ou du vide, le comportement normal est conservé).
- Ne marche pas dans les fenêtres « Ouvrir / Enregistrer sous » des applications.
