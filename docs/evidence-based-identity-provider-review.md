# Identidad y memoria: revisión de otros sistemas

> Revisión de diseño del 11 de noviembre de 2026. Se han leído documentación oficial y rutas concretas de código. No se han instalado los proveedores, ejecutado sus tests ni comparado sus respuestas a Bruna. Las conclusiones sobre AgentMemory son decisiones de diseño, no resultados medidos.

## Qué estamos comparando

El [diseño de AgentMemory](evidence-based-identity-design.md) separa observación, valor tipado, candidato, decisión de identidad y recuperación. El objetivo de esta revisión es encontrar mecanismos reutilizables y detectar dónde nuestra propuesta repite riesgos existentes.

La identidad de una persona, la actualización de su trabajo y una inferencia sobre sus intereses son tres decisiones distintas. Que un producto haga bien una no demuestra que resuelva las otras. Tampoco un resultado agregado de recall demuestra que conserve correctamente el día de una fecha.

## Comparación directa

| Sistema | Qué se ha verificado | Qué adoptar | Límite de la comparación |
|---|---|---|---|
| **Supermemory** | Documentos fuente separados de memorias; relaciones de actualización, ampliación y derivación; revisión de inferencias. | Evolución explícita y revisión con deshacer; mantener contexto suficiente de cada asunto. | El motor del servidor no es público. Su revisión de inferencias no es un verificador de identidad. |
| **Graphiti** | Claves configurables por tipo; consultas exactas y semánticas; resolución exacta/fuzzy; LLM para los no resueltos. | Búsqueda por claves y candidatos acotados; contexto para ambigüedades; validación de respuestas estructuradas. | Existen atajos automáticos exact/fuzzy. El LLM no revisa cada equivalencia. |
| **Neo4j Agent Memory** | Ontología, alias por grupos, candidatos por tipo/scope y bandas merged/review/created. Protecciones para prefijos y nombres poco discriminantes. | Contrato por tipo, bloqueo en la consulta, bandas de decisión, contexto independiente. | Puede confirmar a partir de reglas de nombre o mezcla de similitudes; no equivale al veto por valores tipados que proponemos. |
| **Hindsight** | Nombre, coocurrencia y proximidad temporal; vecinos comunes penalizados; protecciones numéricas; etiquetas exactas; recuperación por varias vías. | Contexto selectivo, autoridad del input, límites de candidatos, evidencia de las observaciones y cobertura de recuperación. | Su decisión de entidad sigue siendo una heurística de score; su propia documentación describe absorciones incorrectas. |
| **Cognee** | Alineamiento con ontologías RDF/OWL; estrategia de matching intercambiable; razonamiento opcional. | Separar el vocabulario del dominio de sus mecanismos de matching; anclar tipos a conceptos definidos. | Alinear una mención con una clase no prueba identidad de una instancia. Su estrategia fuzzy no proporciona igualdad de fechas. |
| **Mem0 OSS** | En la implementación TypeScript inspeccionada, memorias enlazadas a un almacén de entidades; búsqueda reforzada por esas conexiones. | Separación entre texto de memoria y acceso por entidades; reducir la influencia de entidades que enlazan demasiadas memorias. | También hay reutilización automática por similitud semántica. OSS y Platform no deben tratarse como la misma implementación. |
| **Honcho** | Identidades de peers dadas por la aplicación; mensajes separados de conclusiones y representaciones; perspectivas observer/observed. | Aprovechar IDs del host y conservar de quién procede una afirmación y sobre quién trata. | Está orientado a participantes y sus representaciones; no demuestra un resolver general de entidades ni igualdad temporal. |

## Supermemory: qué aporta y qué podemos inspeccionar

La documentación distingue el material que recibe de las memorias que extrae. Una memoria posterior puede actualizar otra, añadirle detalle o derivarse de varias. También describe procesamiento con contexto de conversación y permite consultar información relacionada. Estos patrones son útiles para preservar continuidad del evento sin confundirla con identidad. [Modelo de memorias](https://supermemory.ai/docs/concepts/graph-memory).

Su revisión permite aprobar, declinar y deshacer decisiones sobre memorias inferidas. Una inferencia sin revisar puede estar en búsqueda; declinarla la retira. Adoptaríamos el ciclo de revisión, manteniendo separadas la aprobación de una conclusión y la confirmación de identidad. [Revisión de inferencias](https://supermemory.ai/docs/recall/memory-review).

El servidor local se distribuye como binario y su código pertenece a una base no pública. El repositorio público contiene SDKs y otros componentes abiertos. Por tanto, no hemos auditado sus reglas internas de identidad, normalización de fechas o transacciones. Autoalojamiento y código abierto son propiedades distintas. [Supermemory local](https://supermemory.ai/docs/self-hosting/overview).

## Graphiti: buenos candidatos y fallback contextual

El código ofrece claves compuestas configuradas por tipo y vías de búsqueda por atributos, nombre y vector. Después intenta resolver por clave, nombre normalizado y similitud MinHash/Jaccard; lo pendiente puede pasar al LLM con episodios como contexto. Los nombres cortos o poco informativos tienen protecciones en la vía fuzzy.

Adoptamos la separación de rutas y las consultas de candidatos acotadas. Ajustamos su autoridad: en AgentMemory una semejanza de nombre se conserva como señal. Además, cuando varios registros comparten una clave supuestamente única, nuestra propuesta deja una ambigüedad; la ruta de claves inspeccionada en Graphiti ordena candidatos y escoge uno. [Helpers de deduplicación](https://github.com/getzep/graphiti/blob/e911422602068a0dc28e72a1961698269e9aefb2/graphiti_core/utils/maintenance/dedup_helpers.py), [búsqueda y adjudicación](https://github.com/getzep/graphiti/blob/e911422602068a0dc28e72a1961698269e9aefb2/graphiti_core/utils/maintenance/node_operations.py).

Esto no justifica incorporar MinHash ahora. Primero necesitamos consultas acotadas y medir su coste; un índice aproximado adicional sólo tendría sentido si el volumen lo exige.

## Neo4j Agent Memory: la comparación más cercana

Su resolver por ontología trabaja dentro del tipo y admite tres bandas de resultado. Utiliza grupos de alias, contexto y señales de nombre/vector. La implementación actual contiene precauciones para prefijos, siglas y nombres cortos; también evita tratar el contexto de dos menciones del mismo origen como corroboración independiente. Es más elaborado que trasladar nuestro FuzzyMatchEntityMatcher a otra clase.

Adoptamos esas fronteras de tipo, contexto y consulta. Conservamos una exigencia propia: una contradicción de valores o claves confiables no se compensa con una puntuación. Su política por bandas y nuestra política por evidencia no son equivalentes. [Resolver inspeccionado](https://github.com/neo4j-labs/agent-memory/blob/9d45af5529ecb5f2fe352013468a11087f3163d4/src/neo4j_agent_memory/resolution/ontology.py), [configuración oficial](https://neo4j.com/labs/agent-memory/reference/configuration-providers/#resolution-configuration).

## Hindsight: contexto que discrimina

El resolver mezcla nombre, vecinos y cercanía temporal. Descarta candidatos incompatibles por palabras/números y reduce el peso de vecinos muy comunes. También excluye etiquetas controladas del fuzzy y permite preservar nombres explícitos aportados por el cliente. Esto respalda distinguir entradas autorizadas de nombres sugeridos por el extractor, y evitar que compartir conversación o empresa se convierta en identidad. Sus guardas numéricas son útiles, pero no sustituyen una representación de calendario. [Resolver](https://github.com/vectorize-io/hindsight/blob/289fb421bdb438861480720e947874551ac42ff7/hindsight-api-slim/hindsight_api/engine/memories/pg/entity_resolver.py).

La recuperación documentada combina significado, palabras, grafo y tiempo mediante RRF y una fase de ranking. AgentMemory ya tiene RRF para vector/full-text en HybridRetriever: reutilizamos esa base y comprobamos la cobertura de evidencia del evento, en vez de crear otro combinador. [Recuperación](https://github.com/vectorize-io/hindsight/blob/289fb421bdb438861480720e947874551ac42ff7/hindsight-docs/docs/developer/retrieval.md).

Las observaciones consolidadas mantienen referencias a hechos y citas. Ese patrón puede inspirar una futura capa de conclusiones; no se incorpora a las fases actuales de identidad ni se interpreta el número de apoyos como garantía de verdad. [Observaciones](https://github.com/vectorize-io/hindsight/blob/289fb421bdb438861480720e947874551ac42ff7/skills/hindsight-docs/references/developer/observations.md).

## Cognee: ontología e identidad no son la misma operación

El resolver RDF permite localizar conceptos de una ontología y obtener su subgrafo. La estrategia inspeccionada busca igualdad de texto y después usa difflib para la aproximación. El razonamiento OWL es opcional y materializa consecuencias de la ontología.

Adoptamos un contrato de dominio intercambiable, con tipos y restricciones. No necesitamos importar un razonador completo para decidir si una fecha es la misma. Las equivalencias semánticas de un vocabulario tampoco autorizan fusionar dos personas que pertenecen a la misma clase. [Estrategia de matching](https://github.com/topoteretes/cognee/blob/8853378eae5366addb064e758e560a456146ae1a/cognee/modules/ontology/matching_strategies.py), [resolver RDF](https://github.com/topoteretes/cognee/blob/8853378eae5366addb064e758e560a456146ae1a/cognee/modules/ontology/rdf_xml/RDFLibOntologyResolver.py).

## Mem0 OSS y Honcho: dos separaciones aprovechables

En Mem0 OSS TypeScript, una entidad conserva los IDs de las memorias relacionadas. En búsqueda, esas conexiones pueden reforzar resultados y se descuenta el efecto de entidades con muchos enlaces. Adoptamos la separación de la memoria y sus rutas de acceso. La ruta inspeccionada también permite reutilizar una entidad por similitud semántica alta; esa decisión no se trasladaría a nuestra política de identidad. [Implementación inspeccionada](https://github.com/mem0ai/mem0/blob/f1791daa86d469249c9b002f62644d58a1eb96a0/mem0-ts/src/oss/src/memory/index.ts).

Honcho recibe IDs de participantes de la aplicación y distingue mensajes, conclusiones y representaciones, incluyendo quién observa a quién. Adoptamos la prioridad de la identidad conocida por el host y la atribución de fuente. Es una comparación parcial: no es evidencia de que su sistema resuelva automáticamente el mismo conjunto de objetos y eventos que AgentMemory. [Arquitectura de Honcho](https://github.com/plastic-labs/honcho/blob/17d0701094d3ad3e31ee93baa12b3d6e8f97399e/README.md).

## Cambios concretos en nuestro diseño

| Decisión | Entrega | Aceptación |
|---|---|---|
| Pendientes en POSSIBLE_SAME_AS o registro de decisión separado | IDER-03 | Ningún lector, expansión o exportación los interpreta como equivalencia; pruebas de persistencia, rollback y reinicio. |
| Fuentes correlacionadas no cuentan como corroboración independiente | IDER-02 / 04 | Fuzzy + embedding del nombre, resúmenes y repeticiones no convierten una duda en confirmación. |
| Contexto selectivo y autoridad del host | IDER-02 | Dos personas que comparten empresa/user siguen separadas; una referencia verificada conserva ID, namespace y tipo. |
| Fecha + lugar + presencialidad como evidencia completa del evento | IDER-06 | Bruna Q3 exige los tres datos y sus fuentes; pendiente no concede herencia de otra identidad. |
| Reutilizar el RRF existente y separar relevancia de identidad | IDER-06 / 07 | No cambiar la identidad por ranking; medir cobertura y exactitud de respuesta por separado. |
| Corpus adversario por tipo, orden y contexto | IDER-07 | Comprobar falsos enlaces, enlaces perdidos, valores alterados, pendientes y coste; no sólo palabras en el prompt. |

La primera fila corrige una debilidad concreta del diseño inicial: EntityQueries.GetSameAsEntities lee SAME_AS sin filtrar estado. Añadir nuevos pendientes a esa relación obligaría a hacer depender la garantía de todos sus consumidores. Se propone una representación distinta desde el inicio, sin reinterpretar datos legados ni presentar ese cambio como implementado.

Un pendiente no impide recuperar el hecho original por texto, vectores o procedencia. Impide heredar los hechos de otra entidad como si la identidad estuviera confirmada.

## Qué queda fuera y por qué

- Consolidación continua de creencias, perfiles derivados y dreaming: ideas futuras; amplían el comportamiento y el coste más allá de corregir identidad.
- Un framework OWL o un nuevo almacén: no son necesarios para las políticas de tipo del primer incremento.
- Copiar umbrales numéricos de otro proveedor: sus scores, corpus y costes no están calibrados para nuestro dominio.
- Fusionar físicamente entidades por similitud: dificulta revisión y preservación de evidencia.

## Cómo demostrar la mejora

Usaremos Bruna original y busy, más variantes con fechas 3/4/12, año desconocido, mismo día en eventos distintos, alias erróneos, homónimos y claves contradictorias. Añadimos prefijos ambiguos, nombres con números, vecinos muy comunes, evidencia repetida y cambios de orden/batching. Cada caso especificará la identidad esperada y la evidencia que autoriza decidirla.

La comparación por proveedores requiere fijar versión, modelos, prompts, límites, corpus y etiquetas revisadas. Hasta entonces podemos justificar adoptar una técnica, pero no afirmar que AgentMemory sea más preciso que esos sistemas o que ellos pasen el caso de Pablito.

## Código abierto y adopción

| Repositorio público consultado | Licencia declarada en GitHub | Alcance de esta revisión |
|---|---|---|
| supermemoryai/supermemory | MIT | Componentes públicos y documentación; servidor no público según sus docs. |
| getzep/graphiti | Apache-2.0 | Motor de grafo y resolución. |
| neo4j-labs/agent-memory | Apache-2.0 | Resolver Bolt y documentación. |
| vectorize-io/hindsight | MIT | Resolver y documentación de recuperación/observaciones. |
| topoteretes/cognee | Apache-2.0 | Matching y alineamiento con ontología. |
| mem0ai/mem0 | Apache-2.0 | Ruta OSS TypeScript; no se extrapola a toda Platform. |
| plastic-labs/honcho | AGPL-3.0 | Arquitectura pública de peers y representaciones. |

Esta revisión adopta ideas de arquitectura; no incorpora código de proveedores ni nuevas dependencias. Una incorporación literal de código requiere comprobar la licencia del archivo/componente y conservar sus avisos. La licencia del repositorio público de Supermemory no concede acceso al servidor no público.

## Evidencia y límites

Los enlaces de código anteriores fijan commits. Se comprobó que los blobs inspeccionados coinciden con esos commits. La revisión de los proveedores y la revisión de nuestras consultas son estáticas; no se han ejecutado pruebas de los proveedores, modelos ni Neo4j. La regresión temporal de AgentMemory continúa pendiente de ejecución, y producción no cambia por actualizar estos documentos.
