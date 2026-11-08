# Guida alle quest automatiche

*[English version](AUTO_QUESTS_GUIDE.md)*

In LivingNPC i personaggi possono avere **iniziative proprie**: portare il pane a un amico, andare a studiare in
biblioteca, fare visita a qualcuno che stimano. Non è il giocatore ad avviarle: è il personaggio che, quando è libero,
sceglie da solo cosa fare. Queste iniziative si chiamano **quest automatiche** e si scrivono come **modelli**
(*template*): una quest scritta una volta sola, in modo generico, che ogni personaggio adatto può fare a modo suo.

Questa guida spiega come funzionano, come si scrive un nuovo modello, dove stanno le frasi che i personaggi dicono,
come il gioco sceglie quale frase usare e come aggiungerne o cambiarne. Il riferimento completo dei campi è nel
[riferimento JSON](JSON_REFERENCE.md#automatic-quests-templatesidjson) e nel
[linguaggio dello Studio](STUDIO_COMMANDS.md#automatic-quests-template-and-shared-lines).

## Indice

1. [L'idea in breve](#1-lidea-in-breve)
2. [Cosa serve ai personaggi](#2-cosa-serve-ai-personaggi)
3. [Il ciclo: come un personaggio sceglie e svolge una quest](#3-il-ciclo-come-un-personaggio-sceglie-e-svolge-una-quest)
4. [Scrivere un nuovo modello, passo per passo](#4-scrivere-un-nuovo-modello-passo-per-passo)
5. [I dialoghi: dove sono salvati](#5-i-dialoghi-dove-sono-salvati)
6. [I dialoghi: come vengono scelti](#6-i-dialoghi-come-vengono-scelti)
7. [I dialoghi: crearne e modificarne](#7-i-dialoghi-crearne-e-modificarne)
8. [Personaggi occupati](#8-personaggi-occupati)
9. [Provare e correggere](#9-provare-e-correggere)
10. [Consigli di design e limiti attuali](#10-consigli-di-design-e-limiti-attuali)

---

## 1. L'idea in breve

Una quest normale è scritta per il giocatore e nomina personaggi precisi: *"Elena ti chiede di andare nella cripta"*.
Un modello invece descrive una situazione **senza sapere chi la vivrà**:

> *Un personaggio che ha del pane lo porta a qualcuno a cui vuole bene, a casa sua.*

Dentro il modello non c'è scritto "Elena" né "John": c'è scritto **`self`** (chi fa la quest) e dei **segnaposto**
chiamati *slot* (`friend`, "l'amico"; `house`, "la sua casa"). Quando Elena sceglie questo modello, il gioco riempie gli
slot con valori veri: `self` = Elena, `friend` = John, `house` = la stanza di John. Da quel momento la quest è
**un'istanza** concreta, che va avanti per fasi come una quest normale.

Lo stesso modello, scelto da un altro personaggio, produce una storia diversa. Con pochi modelli ben scritti il mondo
sembra pieno di persone che fanno cose.

## 2. Cosa serve ai personaggi

Nella scheda **Personaggi** dello Studio (o nel file `npcs/<id>.json`):

| Campo | A cosa serve |
|---|---|
| **Automatic quests** (`autoQuests`) | L'interruttore: senza, il personaggio non sceglie mai quest automatiche da solo. Spento di default. |
| **Goal** (`goal`) | Cosa desidera (`help`, `learn`…). I modelli possono preferire chi ha un certo obiettivo. Gli obiettivi si dichiarano nella scheda **Regole → Goals**. |
| **Relations** (`relations`) | Cosa pensa degli altri, da -100 a 100 (anche `player`). Si usano per scegliere chi coinvolgere e cambiano con le quest. |
| **First name, Last name, Age, Sex** | Chi è. I dialoghi li usano: `{friend.first}`, `{friend:lui|lei}`. |
| **Stats** | Le statistiche (felicità, energia…) possono rendere un modello più o meno attraente. |

Nella scheda **Regole → Automatic quests** ci sono tre numeri che valgono per tutto il mondo:

| Campo | Default | Significato |
|---|---|---|
| **Check every** (`checkMinutes`) | 60 | Ogni quanti minuti di gioco i personaggi liberi cercano qualcosa da fare. |
| **At most at once** (`maxActive`) | 3 | Quante quest automatiche possono essere in corso insieme nel mondo. Evita che tutti partano contemporaneamente. |
| **Pause after one** (`pauseDays`) | 1 | Quanti giorni un personaggio riposa dopo averne finita una (ogni modello può dire il suo). |

## 3. Il ciclo: come un personaggio sceglie e svolge una quest

```
 ogni "checkMinutes" di gioco
        │
        ▼
 il personaggio è libero?  ── no ──▶ (segue la sua routine)
 (interruttore acceso, nessuna quest in corso,
  pausa finita, non sta dando una quest busyGiver)
        │ sì
        ▼
 per ogni modello:
   1. "when" vale per lui?            ── no ──▶ scartato
   2. riesco a riempire gli slot?     ── no ──▶ scartato
   3. punteggio ("weight") > 0?       ── no ──▶ scartato
        │
        ▼
 estrazione a sorte pesata sul punteggio  (più punti = più probabilità, mai certezza)
        │
        ▼
 la quest parte: onStart del modello, poi la prima fase
        │
        ▼
 fasi: onStart → (complete / fail) → onComplete / onFail → fase successiva
        │
        ▼
 fine: onComplete o onFail del modello, il personaggio se la ricorda, poi riposa "pauseDays"
```

Alcuni dettagli importanti:

- **Il tempo**: nel gioco il tempo scorre solo quando il giocatore si sposta, aspetta o dorme. Le quest automatiche
  avanzano insieme al tempo, **anche lontano dal giocatore**: se il giocatore dorme otto ore, Elena nel frattempo può
  aver portato il pane e averne parlato a qualcuno.
- **La scelta non è deterministica**: un modello con punteggio 40 e uno con punteggio 10 vengono scelti circa 4 volte
  su 5 e 1 su 5. Questo rende i personaggi meno prevedibili.
- **Gli slot si riempiono a caso** tra i candidati validi: se due personaggi vanno bene come `friend`, ne viene preso
  uno dei due.
- **I personaggi a turno**: a ogni controllo si parte da un personaggio diverso, così nessuno ha sempre la precedenza
  quando si raggiunge il massimo di quest contemporanee.
- **La memoria**: ogni personaggio ricorda le ultime 16 quest automatiche fatte (quale, com'è finita, quando, con chi).
  La condizione `did(...)` la legge: per esempio "non rifarla se l'hai già fatta questa settimana".
- **I salvataggi**: una quest in corso viene salvata con il personaggio (modello, fase, valori degli slot) e riprende
  esattamente da lì.

## 4. Scrivere un nuovo modello, passo per passo

### 4.1 Creare il file

Nello Studio, scheda **Quests & messages**, premi **+ Auto quest** e scegli un id (per esempio `t_bring_bread`;
diventa il nome del file `templates/t_bring_bread.json`). Lo Studio scrive uno scheletro funzionante: da lì si modifica.
I modelli compaiono nella lista a sinistra nel gruppo **Automatic quests**, con la lettera **A**.

Costruiamo per intero l'esempio di Aetherhall: *Elena porta il pane a chi le sta simpatico*.

### 4.2 Titolo e pausa

```python
template t_bring_bread:
    title = "{self} brings bread to {friend}"
    pauseDays = 1
```

Il titolo serve a te: lo vedi nel pannello di debug (F12), con i nomi già sostituiti ("Elena brings bread to John").
`pauseDays` è il riposo dopo questa quest; se manca vale quello delle Regole.

### 4.3 Chi può sceglierlo: `when`

```python
    when:
        hasItem(bread, target=self)
        not:
            any(activity(self, sleep), activity(self, work))
```

Le condizioni del `when` parlano di **`self`**: "ha del pane, e non sta dormendo né lavorando". Tutte le righe devono
essere vere. Se il `when` è falso il modello non viene neanche considerato. Qui gli slot non si possono ancora usare
(non sono ancora riempiti): le condizioni sugli altri personaggi vanno negli slot.

### 4.4 Quanto lo desidera: `weight`

```python
    weight:
        base = 5
        goals = {"help": 30}
        relations = {"friend": 0.5}
```

Il punteggio è una somma:

| Voce | Esempio | Significato |
|---|---|---|
| `base` | `5` | Punti di partenza (se manca: 10). |
| `stats` | `{"happiness": 0.2}` | + statistica di `self` × numero (felicità 80 → +16). |
| `goals` | `{"help": 30}` | + 30 se `self` ha l'obiettivo `help`. |
| `relations` | `{"friend": 0.5}` | + relazione di `self` verso lo slot × numero (relazione 40 con John → +20). |

Con un numero negativo si scoraggia invece di incoraggiare (`{"energy": -0.3}`: più è stanco, meno lo fa). Se il totale
è 0 o meno il modello non viene scelto.

### 4.5 Chi e cosa coinvolge: gli slot

```python
    slot friend:
        type = npc
        free = true
        when:
            relation(self, it, ">=", 10)
    slot house:
        type = place
        of = friend
        place = home
```

Ogni slot è un "buco" che il gioco riempie quando il modello viene scelto. I tipi:

| `type` | Cosa diventa | Come si sceglie |
|---|---|---|
| `npc` | un altro personaggio | uno a caso tra quelli per cui vale il `when` (mai `self`, mai un personaggio già in un altro slot). Con `free = true` solo chi è libero ([sezione 8](#8-personaggi-occupati)). |
| `place` | un luogo di un personaggio | il luogo con nome `place` (`home`, `work`…) del personaggio `of` (`self` se manca, oppure uno slot sopra). |
| `node` | un luogo della mappa | uno a caso tra `nodes = [...]` (tutti, se manca) per cui vale il `when`. |
| `item` | un oggetto | uno a caso tra `items = [...]` (tutti, se manca) per cui vale il `when`. |

Nel `when` di uno slot la parola **`it`** indica il candidato che si sta provando: `relation(self, it, ">=", 10)`
significa "un personaggio verso cui io ho almeno 10". Uno slot può usare gli slot scritti **sopra** di lui (`house` usa
`friend`), non quelli sotto. Al massimo 6 slot.

Se uno slot non trova nessun candidato, il modello è scartato per quel personaggio in quel momento.

### 4.6 Le fasi

```python
    stage go:
        text = "{self} walks to the room of {friend}"
        onStart:
            moveNpc(self, house, forMinutes=180)
        complete:
            npcAt(self, house)
        fail:
            elapsed(hours=3)

    stage give:
        text = "{self} gives the bread to {friend}"
        onStart:
            moveNpc(self, house, forMinutes=60)
        complete:
            elapsed(minutes=30)
        onComplete:
            tell(thanks)
            takeItem(bread, target=self)
            giveItem(bread, target=friend)
            modRelation(friend, self, 10)
            modStat(happiness, 5, target=self)
```

Le fasi funzionano come quelle delle quest normali, con qualche aggiunta:

- **`onStart`** parte quando la fase comincia: è il posto giusto per far muovere il personaggio (`moveNpc` lo manda in
  un luogo per un certo tempo, poi torna alla sua routine).
- **`complete`** e **`fail`** sono condizioni controllate continuamente; quella che diventa vera per prima decide.
  Una fase senza nessuna delle due non finisce mai: **metti sempre un `fail` a tempo** (`elapsed(...)`), così un
  personaggio bloccato (strada chiusa, amico sparito) prima o poi rinuncia.
- **`elapsed(...)`** senza quest conta dall'inizio della fase (o dall'inizio della quest con `since="quest"`).
- **`chance(70)`**: vale nel 70% dei casi. Il numero viene tirato **una volta sola all'inizio della fase**, quindi resta
  stabile: si usa in coppia per dividere successo e fallimento:

  ```python
      stage study:
          complete:
              elapsed(hours=2)
              chance(70)
          fail:
              elapsed(hours=2)
              not:
                  chance(70)
  ```

- In tutti i comandi e le condizioni si scrive **`self`** e il **nome degli slot** al posto degli id: `giveItem(bread,
  target=friend)`, `npcAt(self, house)`, `modRelation(friend, self, 10)`.
- `next = <fase>` salta a un'altra fase; senza, si va alla successiva; `next = end` chiude la quest.

### 4.7 Inizio e fine del modello

```python
    onStart:
        tell(intro)
    onComplete:
        tell(proud, everywhere=true)
    onFail:
        tell(tired, everywhere=true)
```

`onStart` del modello parte prima della prima fase. `onComplete` parte quando l'ultima fase si completa; `onFail`
quando una fase fallisce (dopo l'`onFail` della fase stessa). Sono il posto naturale per le **conseguenze**: relazioni,
statistiche, oggetti, flag che altre quest possono leggere, perfino `startQuest(...)` di una quest del giocatore.

### 4.8 Salvare

**Save** (Ctrl+S) nello Studio scrive `templates/t_bring_bread.json`. Nel gioco premi **F5** per ricaricare i file
senza perdere la partita in corso.

## 5. I dialoghi: dove sono salvati

Le frasi delle quest automatiche stanno in **due posti**:

| Dove | Cosa | Chi le può dire |
|---|---|---|
| Dentro il modello, nel blocco **`texts:`** → file `templates/<id>.json`, campo `"texts"` | Le frasi di **quella** quest: possono nominare gli slot (`{friend}`, `{house}`). | Solo quel modello. |
| Nei file **`lines/<id>.json`** (nello Studio: documento **Shared lines**, lettera **L**) | Frasi **condivise**: stanchezza, saluti, soddisfazione… | Tutti i modelli, e anche quest e messaggi normali. Conoscono solo `self`, non gli slot. |

Nel file JSON di un modello il blocco ha questa forma:

```json
"texts": {
  "intro": [
    { "when": { "type": "relation", "from": "self", "to": "friend", "op": ">=", "value": 30 },
      "lines": [ "I baked too much bread: {friend.first} will love it.", "Off to {friend}, with a warm loaf!" ] },
    { "lines": [ "Maybe {friend} would like some bread." ] }
  ],
  "thanks": [ "Here, {friend.first}: still warm." ],
  "talk": [ "Not now, dear: I am bringing bread to {friend}." ]
}
```

e un file di frasi condivise, `lines/proud.json`:

```json
{ "id": "proud", "variants": [
    { "when": { "type": "stat", "stat": "intelligence", "op": ">=", "value": 30, "target": "self" },
      "lines": [ "{self.first} closes the book: another chapter mastered." ] },
    { "lines": [ "{self.first} smiles: the spells finally make sense." ] } ] }
```

Due parole che userò da qui in poi:

- un **gruppo** è un insieme di frasi con un nome (`intro`, `thanks`, `talk`, `proud`);
- una **variante** è una parte del gruppo con una sua condizione (`when`) e le sue frasi (`lines`).

**Le traduzioni**: ogni frase può essere un testo semplice o un oggetto per lingua, come tutti i testi del gioco:
`{ "it": "Vado da {friend}!", "en": "Off to {friend}!" }`. La lingua mostrata è quella di `rules.json`.

**Nei salvataggi** non viene copiata la frase: viene salvato il suo riferimento (modello, gruppo, variante e un codice
calcolato dal testo) più i nomi di chi c'era negli slot. Così il diario di una partita salvata segue le tue correzioni
e la lingua scelta.

## 6. I dialoghi: come vengono scelti

### 6.1 Quando si parla

Una frase viene detta in due casi.

**Con il comando `tell(gruppo)`**, scritto in un `onStart`, `onComplete` o `onFail`:

```python
tell(intro)                       # la dice self
tell(thanks, npc=friend)          # la dice l'amico
tell(proud, everywhere=true)      # la scrive anche se il giocatore non c'è
```

La frase compare nel **registro dei messaggi** in basso, nella forma *"Elena: Off to John, with a warm loaf!"*.
**Senza `everywhere=true` compare solo se il giocatore è nello stesso luogo di chi parla**: le cose che succedono
lontano non si sentono. Con `everywhere=true` arriva sempre (utile per notizie, voci di corridoio, esiti importanti).

**Quando il giocatore parla con un personaggio impegnato.** Se il modello ha un gruppo che si chiama **`talk`** e il
giocatore clicca sul personaggio mentre sta facendo quella quest, il personaggio risponde con una frase di `talk`
(*"Not now, dear: I am bringing bread to John"*). Vale solo se nessun messaggio (`event` con `trigger = talk`)
risponde già a quel personaggio in quel momento: i messaggi scritti a mano hanno la precedenza.

### 6.2 Quale gruppo

`tell(intro)` cerca prima un gruppo `intro` **nel modello**, poi un file **`lines/intro.json`**. Quindi un modello può
"coprire" una frase condivisa con una sua versione più specifica usando lo stesso nome.

### 6.3 Quale variante

Le varianti si leggono **dall'alto in basso** e vince **la prima la cui condizione è vera in quel momento**. Una
variante senza `when` è sempre vera: mettila **per ultima**, come caso generale.

```python
        intro:
            variant if relation(self, friend, ">=", 30):     # 1. se gli vuole molto bene
                "I baked too much bread: {friend.first} will love it."
                "Off to {friend}, with a warm loaf!"
            variant if stat(happiness, "<", 30, target=self):  # 2. se è giù di morale
                "I'll bring some bread to {friend}. Maybe it cheers me up."
            variant:                                          # 3. altrimenti
                "Maybe {friend} would like some bread."
```

Le condizioni vengono valutate **nel momento in cui la frase viene detta**, non quando la quest è partita: se nel
frattempo il personaggio si è intristito, parla da triste. Nelle condizioni si possono usare `self` e gli slot.

Se nessuna variante è vera, il personaggio non dice niente.

### 6.4 Quale frase

Dentro la variante vincente si prende **una frase a caso**. Il "caso" dipende dall'ora del gioco, dal gruppo e da chi
parla: nello stesso minuto lo stesso personaggio dice la stessa frase, in momenti diversi frasi diverse. Più frasi
metti in una variante, meno il personaggio sembra ripetersi.

### 6.5 I segnaposto

Prima di essere mostrata, la frase viene completata con i nomi veri:

| Scrivi | Diventa | Esempio |
|---|---|---|
| `{self}` | il nome di chi fa la quest | Elena |
| `{friend}` | il valore dello slot: un personaggio → il nome; un luogo → il titolo; un oggetto → il nome | John / Stanza di John / Pane |
| `{friend.first}` | il nome di battesimo | John |
| `{friend.last}` | il cognome | Hale |
| `{friend.full}` | nome e cognome | John Hale |
| `{friend.age}` | l'età | 15 |
| `{friend:lui\|lei\|loro}` | una parola in base al sesso: maschio, femmina, altro | lui |
| `{npc}` | chi sta parlando (o, nei messaggi normali, con chi parla il giocatore) | Elena |

Per la terza forma il sesso viene dal campo **Sex** del personaggio (`m`, `f`, `x`). Se la terza parola manca, per
`x` si usa la prima. È il modo per scrivere frasi corrette in italiano: *"arrabbiat{friend:o|a}"*,
*"{friend:il|la} giovane {friend.first}"*.

`{npc.first}`, `{npc:lui|lei}` e gli altri funzionano anche nei messaggi e nelle quest normali.

## 7. I dialoghi: crearne e modificarne

### 7.1 Aggiungere frasi a un modello

Apri il modello nello Studio e scrivi nel blocco `texts:`. Le frasi sono testi tra virgolette, una per riga.

**Un gruppo semplice** (una sola variante, sempre valida): le frasi direttamente sotto il nome.

```python
    texts:
        thanks:
            "Here, {friend.first}: still warm."
            "Fresh from the oven, {friend.first}."
```

**Un gruppo con varianti**: blocchi `variant if <condizione>:` e, in fondo, `variant:`.

```python
        intro:
            variant if relation(self, friend, ">=", 30):
                "Off to {friend}, with a warm loaf!"
            variant:
                "Maybe {friend} would like some bread."
```

**Una frase tradotta**: un oggetto al posto del testo.

```python
            {"it": "Vado da {friend}!", "en": "Off to {friend}!"}
```

Poi usa il gruppo da qualche parte con `tell(nome)` (oppure chiamalo `talk`) e salva.

### 7.2 Frasi condivise

Per una frase che serve a tanti modelli, scrivi un blocco `lines` (in un documento qualsiasi; viene salvato in
`lines/<id>.json` e da quel momento compare nel documento **Shared lines**):

```python
lines tired:
    variant if stat(energy, "<", 20, target=self):
        "{self.first} yawns."
        "{self.first} can barely keep {self:his|her} eyes open."
    variant:
        "{self.first} stretches."
```

Nelle frasi condivise si può usare solo **`self`** (chi parla) e `{npc}`: gli slot non esistono, perché la frase non sa
in quale modello verrà usata. Si possono dire anche da una quest o da un messaggio normale, indicando chi parla:
`tell(tired, npc=elena)`.

### 7.3 Modificare

Basta cambiare il testo e salvare (poi F5 nel gioco). Da sapere:

- **I messaggi già nel diario di una partita salvata** che usavano la vecchia frase non si trovano più e vengono tolti
  dal diario quando carichi quel salvataggio (il resto della partita non cambia). Lo stesso succede se sposti una
  variante più su o più giù.
- **Un segnaposto sbagliato** (`{frend}` invece di `{friend}`) è un errore: lo Studio non lo segnala, ma il gioco sì,
  nel rapporto dei contenuti (`content_report.txt`, o **LivingNPC ▸ Validate Content** in Unity), con file e riga.
  Lasciato così, la frase mostrerebbe `{frend}` così com'è.
- **Le parentesi graffe** nei testi dei modelli sono riservate ai segnaposto: non usarle per altro.

### 7.4 Un trucco per i dialoghi credibili

Scrivi le varianti pensando a **chi è** il personaggio, non solo alla situazione. Le condizioni possono leggere le sue
statistiche, la relazione con lo slot, l'obiettivo, l'età, il sesso, cosa ha fatto di recente (`did`). Un modello con
tre varianti ben scelte e tre frasi ciascuna sembra già scritto per ogni personaggio.

## 8. Personaggi occupati

Un personaggio è **occupato** quando:

1. sta facendo una quest automatica sua;
2. è uno slot della quest automatica di qualcun altro (per esempio John mentre Elena gli porta il pane);
3. ha dato al giocatore una quest con **`busyGiver = true`** che è ancora attiva.

Un personaggio occupato non sceglie nuove quest automatiche (casi 1 e 3) e non viene preso negli slot con
**`free = true`**. Le quest del giocatore invece non si bloccano da sole: per evitare che un personaggio occupato offra
una quest, metti la condizione **`busy`** nel messaggio che la offre:

```python
quest crypt:
    giver = elena
    busyGiver = true          # mentre la cripta è in corso, Elena non fa quest automatiche

event ev_crypt_offer:
    trigger = talk
    npc = elena
    when:
        quest(crypt, notStarted)
        not:
            busy(elena)       # e non la offre mentre è impegnata
```

Altre condizioni utili: `doing(elena, t_bring_bread)` (sta facendo proprio quel modello, eventualmente a una certa fase)
e `did(elena, t_bring_bread, completed, days=7)` (l'ha finito negli ultimi 7 giorni).

## 9. Provare e correggere

**Il pannello F12**, pagina del personaggio, sezione *Automatic quests*:

- se è acceso, il suo obiettivo e le relazioni (con pulsanti +10 / -10);
- la quest in corso: fase, da quanto tempo, i valori degli slot, le condizioni di `complete` e `fail` valutate ora,
  ramo per ramo, e un pulsante **Stop**;
- l'elenco dei modelli con il motivo per cui potrebbe sceglierli o no: *can pick it, score 45* — *its when does not
  hold* — *nobody / nothing fits slot friend* — *score 0*; con il pulsante **Start** per farlo partire subito;
- le ultime quest fatte e com'è finita.

**La console** (sempre in F12) accetta gli stessi comandi dello Studio:

```
setAutoQuests(elena, true)        accende / spegne
startAuto(elena, t_bring_bread)   la fa partire subito (ignora when, punteggio e pausa)
stopAuto(elena)                   la interrompe
modRelation(elena, student_1, 20)
setGoal(elena, help)
advanceTime(60)                   fa passare un'ora: i personaggi liberi controllano ogni checkMinutes
```

**Il rapporto dei contenuti** dice gli errori di scrittura (slot inesistenti, segnaposto sbagliati, obiettivi non
dichiarati) con il file e il punto esatto.

## 10. Consigli di design e limiti attuali

**Consigli**

- **Pochi modelli, ben riempiti.** Meglio cinque modelli con varianti ricche che venti modelli con una frase ciascuno.
- **Sempre un'uscita a tempo** (`fail: elapsed(...)`) in ogni fase che aspetta qualcosa.
- **Rispetta la routine**: escludi nel `when` i momenti in cui il personaggio dovrebbe essere altrove (lavoro, sonno),
  oppure accetta che una quest lo distolga, se è quello che vuoi raccontare.
- **Fai lasciare tracce**: relazioni, statistiche, oggetti, flag. Sono quelle a rendere visibili le quest automatiche
  anche quando il giocatore non era presente, e le altre quest (anche del giocatore) possono reagire.
- **Usa `everywhere=true` con parsimonia**: solo per gli esiti che il giocatore deve sapere, altrimenti il diario si
  riempie.
- **Tieni d'occhio `maxActive`**: con molti personaggi, alzalo poco alla volta.

**Limiti di oggi**

- I personaggi parlano **nel registro dei messaggi**, non con la finestra di dialogo: niente scelte del giocatore dentro
  una quest automatica. Per coinvolgere il giocatore, un modello può avviare una quest normale (`startQuest`) o
  impostare un flag che un messaggio scritto a mano usa.
- Il giocatore non può essere uno slot (si può però leggere e cambiare la relazione con lui: `relation(self, player, …)`).
- Al massimo 6 slot per modello; ogni personaggio ricorda le ultime 16 quest automatiche.
- I comandi `addRoutine` e `setRoutine` vogliono l'id di un personaggio vero, non `self` o uno slot (`moveNpc`,
  `setPlace` e `removeRoutine` invece li accettano).
