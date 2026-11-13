# Independent identity gold: sources and licences

The slice files in this folder hold identifiers and SHA-256 hashes only. No corpus text is redistributed here: the
scenario tool reads the text from a downloaded copy at run time and refuses a copy whose sentences do not match the hashes.

## ECB+ (`ecbplus-slice-v1.json`)

- Agata Cybulska and Piek Vossen. 2014. *Using a sledgehammer to crack a nut? Lexical diversity and event coreference
  resolution.* In Proceedings of LREC 2014.
- Source: <https://github.com/cltl/ecbPlus> (folder `ECB+_LREC2014`: `ECB+.zip`, `ECBplus_coreference_sentences.csv`).
- Licence of the annotations: Creative Commons Attribution 3.0 Unported
  (<http://creativecommons.org/licenses/by/3.0/legalcode>), (c) Agata Cybulska, Piek Vossen and VU University Amsterdam.
  The article texts are third-party news found through Google Search; they are not copied here.
- What we use: topic, document and sentence numbers, the annotators' cross-document instance identifiers and descriptors,
  and hashes of the selected sentences.

## ConEL-2 (`conel2-slice-v1.json`)

- Hideaki Joko and Faegheh Hasibi. 2022. *Personal Entity, Concept, and Named Entity Linking in Conversations.* In
  Proceedings of CIKM 2022.
- Source: <https://github.com/informagi/conversational-entity-linking-2022>
  (`dataset/Conversational_Entity_Linking_Annotations/ConEL22_EL_Test.json`).
- Licence of the annotations: MIT, (c) 2022 Informagi. The dialogues come from Wizard of Wikipedia (Dinan et al., 2019)
  under that dataset's own terms; they are not copied here.
- What we use: dialogue ids, user-turn numbers and hashes of those turns.
