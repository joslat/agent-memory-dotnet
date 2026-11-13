# Identidad en AgentMemory: fallo de Bariloche, auditoria y correccion

Revision: 13 de noviembre de 2026. Rama `feature/evidence-based-identity`, HEAD `b2dea0b`; cambios de implementacion existentes sin commit. Auditoria de codigo, trazas retenidas y codigo abierto de proveedores fijado a commits. **La aceptacion del caso completo esta reabierta. No hay una correccion de produccion nueva en esta auditoria.**

## Resultado que debe explicarse

El caso original contiene 49 intervenciones, ocho sesiones y tres preguntas. La ejecucion termino, pero **no aprobo**: la preparacion del go-live no recibe Cordoba ni la obligacion de asistir en persona. Las seis comprobaciones seleccionadas de almacenamiento pasan: go-live el 11, Cordoba, viaje 9-13, boda el 12, capacitacion el 3 y code freeze el 4. Esto demuestra esos seis hechos; no la integridad de todas las operaciones del writer ni todos los vinculos.

La salida final tambien propone capacitacion el 8 y una opcion remota sin respaldo suficiente. Ese es un fallo adicional de la respuesta, distinto de corromper la fecha guardada. No debe ocultarse en un promedio de palabras encontradas.

Evidencia reproducible y limites de la ejecucion anterior: [resultados](evidence-based-identity-evaluation-results.md). Los nuevos experimentos reales conservaran el escenario original y el presupuesto acumulado, sin reiniciar reservas. No se han ejecutado modelos ni servicios de competidores en esta auditoria.

## 1. Que paso realmente con la identidad

1. Una intervencion introduce el go-live del sistema de facturacion y su fecha. Se conserva una observacion del evento A y su fuente.
2. Otra intervencion dice que el cutover ocurre en la oficina de Cordoba y requiere presencia fisica. El writer conserva ambos hechos y una nueva observacion del evento B.
3. La busqueda de candidatos encuentra A: **el problema observado no es que el candidato correcto quedase fuera del top-K**.
4. `IdentityIngestion.PlanAsync` arma las fuentes con `window.Targets.Concat(window.Context)`. En esta ruta el host entrega una intervencion por llamada; el contexto anterior necesario no esta en esa ventana. No existe aqui una carga de las fuentes historicas del candidato.
5. `IdentityContextPayload.TryEncode` incluye en `candidate.sourceIds` solo los IDs presentes entre las fuentes ofrecidas. La fuente original de A existe, pero no se ha ofrecido: la lista que ve el modelo queda vacia. Su descripcion tambien esta vacia.
6. El adjudicador recibe la frase del cutover y un candidato de igual nombre, sin la intervencion que introdujo el evento. Devuelve `uncertain`, con fin normal y 29 tokens de salida. **No es truncamiento, fallo de JSON ni agotamiento de tokens.**
7. El sistema conserva B separadamente, con propuesta pendiente hacia A. `EventDetailQueries.Read` sigue los vinculos actuales a entidades; no utiliza una propuesta pendiente como igualdad. La fecha lleva a A, lugar/presencia llevan a B: ese recorrido no los conecta.

El falso negativo de continuidad empieza durante la escritura/resolucion. Su consecuencia aparece durante la recuperacion. No es otra fusion fuzzy de December 12 con December 3.

**Lo que estuvo mal:** se implementaron la decision conservadora y el recorrido seguro, pero no se completo la entrega de evidencia entre ambos. No basta con disponer de un campo de procedencia; hay que cargar el texto autorizado cuando la decision lo necesita. El writer ve memorias almacenadas que el adjudicador no recibe necesariamente: ambos pueden interpretar la misma mencion con distinta informacion.

**Lo que no debemos romper:** negar igualdad por nombre solo, preservar las observaciones originales y no cruzar propuestas pendientes como si fueran confirmaciones. El resultado `Pending` es correcto ante la informacion ofrecida; fabricar esa escasez de informacion en un caso resoluble es una carencia de nuestra integracion.

La hidratacion de fuentes es una correccion fundada, pero **todavia no se ha demostrado que baste** para resolver este caso con el modelo real. Debe incluir relacion con la mencion actual, orden/tiempo de los mensajes y cobertura del candidato. Dos eventos del mismo nombre deben seguir separados cuando las fuentes no permitan decidir.

## 2. Segundo fallo: contrato del writer y cobertura de vinculos

El escritor pide operaciones y citas en el prompt, pero `AskAsync` llama a `CompleteAsync` con `json:false`; no envia un esquema de operaciones al proveedor. El parser puede leer un objeto JSON y rechazar parte de sus operaciones. Los reintentos actuales se activan cuando no se puede leer el JSON, no por esos rechazos parciales.

La traza real registra:

| Turno | Respuesta/problema | Efecto observado |
|---|---|---|
| 36 | `op=entity`, cuando la accion debia ser `add` | Se rechaza la observacion EVENT de la boda. Sus hechos de fecha/lugar se conservan sin vinculo de sujeto a esa observacion. |
| 43 | Cita propuesta que no aparece como tramo contiguo del mensaje | Se rechaza un hecho sobre escribir el checklist de rollback. |
| Otro hecho del go-live | Hecho del checklist sin observacion EVENT actual | El hecho se guarda, pero no adquiere su vinculo de identidad de sujeto. |

`IdentityIngestion.BindFactsAsync` vincula hechos a observaciones explicitas de esa ingesta y comprueba procedencia. No inventa una observacion ni usa un nombre existente como autoridad. Esa proteccion explica los tres hechos EVENT sin vinculo encontrados; no hace completo el resultado del writer.

Rechazar una operacion invalida es correcto. **Dar por completa la escritura a partir del resultado restante, con el rechazo visible solo en Debug, es insuficiente para este contrato.** Un esquema reduce errores de forma, pero no garantiza citas fieles ni referencias completas. Hacen falta validacion semantica limitada, una correccion acotada cuando proceda y un resultado parcial visible si no se puede corregir.

No se debe convertir silenciosamente `op=entity` en `add`, aceptar citas inventadas ni usar el mensaje entero como evidencia para salvar cualquier afirmacion. La integridad de un hecho aceptado y la cobertura de todos los hechos/vinculos son medidas diferentes.

## 3. Donde nos equivocamos al validar y cerrar

La prueba nativa `Dated_event_reaches_undated_location_and_presence_without_similarity` entrega fecha, lugar y presencia juntos en una misma extraccion. Demuestra el recorrido sobre vinculos existentes; no demuestra la construccion de continuidad entre turnos independientes.

Las pruebas del adjudicador usan respuestas simuladas y fuentes preparadas por el test. Validan protocolo, limites y efectos de una respuesta, pero no garantizan que el host cargue las fuentes anteriores correctas. El corpus progresivo contiene ocho casos; `ProgressiveIdentityPolicyTests` ejecuta directamente solo P00-P04 como oraculos de politica. Los casos superiores con claves de evento proporcionadas por el host no equivalen al caso conversacional sin claves.

Los 26 controles adicionales comprueban el evaluador y su presupuesto. Las 8.040 pruebas del proyecto raiz comprueban muchos contratos. **Ninguna de esas cifras convierte en aprobado el resultado real que falla.** Tampoco es valido contar cero Warning como cero operaciones descartadas.

Hubo ademas un fallo de criterio de cierre: se considero el contexto historico una mejora futura, aunque ya existia un fallo observable de continuidad. Se ha promovido a correccion activa junto al contrato del writer. Se mantiene el historial del cierre anterior; no se borra ni se reinterpretan los fallos como aprobados.

## 4. Comparacion de implementaciones abiertas

La comparacion es estatica, por mecanismos. No es una clasificacion de precision ni una prueba de que otro motor pase Bariloche.

| Motor y codigo inspeccionado | Diferencia respecto al fallo observado | Que adoptar o conservar |
|---|---|---|
| [Graphiti: resolucion](https://github.com/getzep/graphiti/blob/e911422602068a0dc28e72a1961698269e9aefb2/graphiti_core/utils/maintenance/node_operations.py#L703) | El fallback recibe mensaje actual, episodios anteriores, atributos y un resumen del candidato. Nuestra llamada fallida carece de su fuente anterior. | Contexto de ambas menciones y continuidad de episodios, con nuestros limites y permisos. |
| [Graphiti: deduplicacion](https://github.com/getzep/graphiti/blob/e911422602068a0dc28e72a1961698269e9aefb2/graphiti_core/utils/maintenance/dedup_helpers.py#L321) | Tambien confirma ciertos nombres normalizados y coincidencias fuzzy automaticamente. Podria evitar algunos fragmentos con un criterio mas permisivo. | No copiar esa autoridad por similitud: hay que medir las falsas fusiones, especialmente homonimos y eventos repetidos. |
| [Neo4j Agent Memory](https://github.com/neo4j-labs/agent-memory/blob/9d45af5529ecb5f2fe352013468a11087f3163d4/src/neo4j_agent_memory/resolution/ontology.py) | Separa normalizacion, bloqueo por tipo, puntuacion, bandas y agrupacion; incorpora contexto y reglas de nombres. | Conservar busqueda acotada y fronteras por tipo; comprobar que el contexto llegue a la ruta de ingesta real. Sus bandas no sustituyen nuestros vetos de claves/fechas. |
| [Hindsight](https://github.com/vectorize-io/hindsight/blob/289fb421bdb438861480720e947874551ac42ff7/hindsight-api-slim/hindsight_api/engine/memories/pg/entity_resolver.py#L1310) | Usa compatibilidad de tokens, contexto de coocurrencia y proximidad temporal en la decision. Penaliza vecinos poco selectivos. | Aprovechar contexto selectivo para candidatos; no convertir tiempo cercano o coocurrencia en prueba de identidad. |
| [Mem0 OSS, ruta TypeScript](https://github.com/mem0ai/mem0/blob/f1791daa86d469249c9b002f62644d58a1eb96a0/mem0-ts/src/oss/src/memory/index.ts#L482) | Separa memorias y entidades con IDs de memorias enlazadas; reutiliza entidad por nombre exacto o similitud semantica >=0.95. Esos enlaces participan en ranking. | Separar acceso a memorias de igualdad de entidades. No copiar el umbral como autoridad, ni extrapolar esta ruta a Platform u otros lenguajes. |
| [Cognee: matching de ontologia](https://github.com/topoteretes/cognee/blob/8853378eae5366addb064e758e560a456146ae1a/cognee/modules/ontology/matching_strategies.py) | Expone estrategias de matching intercambiables para vocabulario de ontologia. | Tipos y estrategias configurables son utiles. Alinear con una clase no identifica una instancia ni compara fechas. |

Supermemory documenta agrupacion de documentos relacionados para mantener unidades conversacionales y relaciones de ampliacion/actualizacion de memorias. Es una inspiracion para no procesar cada mensaje como una isla. Pero su [servidor autoalojable no tiene codigo publico](https://supermemory.ai/docs/self-hosting/overview); los SDK abiertos no permiten auditar su resolver. Su [modelo documental](https://supermemory.ai/docs/concepts/graph-memory) no prueba que resuelva nuestros casos.

Nuestro diseño de evidencia reversible sigue teniendo sentido. La comparacion revela una implementacion incompleta de su contexto y una dependencia fragil entre writer, observaciones y enlaces. No demuestra que bajar umbrales o sustituir todo el motor sea la solucion.

## 5. Bateria acotada propuesta

Crear un corpus pequeno y versionado: **24 casos base (ocho familias por tres situaciones) y seis casos transversales**. Cada caso tendra textos fuente, orden, timestamps, scope, tipos/subtipos declarados, identidades reales y hechos esperados. El oraculo quedara fuera del prompt.

| Familia | Tres situaciones representativas |
|---|---|
| Fecha | 3/4/12 diferentes; dos formatos de la misma fecha completa; fecha incompleta sin inventar ano. |
| Intervalo | Rangos equivalentes; extremos diferentes o invalidos; solapamiento sin igualdad. |
| Persona | Mismo ID con alias; dos homonimos con claves distintas; nombre solo insuficiente. |
| Organizacion | Nombre legal y alias respaldado; matriz frente a filial; sigla ambigua. |
| Lugar | Cordoba AR frente a ES; alias respaldado de una ciudad; ciudad frente a oficina concreta. |
| Evento | Fecha/lugar/presencia en turnos distintos; dos ediciones del mismo evento; remoto permitido frente a presencia requerida. |
| Objeto con identificador | Misma clave estable; identificadores numericos distintos; producto de nombre parecido a fecha. |
| Tipo/subtipo | Misma etiqueta entre tipos incompatibles; subtipos compatibles declarados por el host; subtipo desconocido sin inventar autoridad. |

Los seis transversales cubriran: ingesta junta frente a secuencial/reinicio; varios candidatos realmente ambiguos; owners y permisos compartidos; correccion/revocacion con historia; operaciones/citas invalidas y referencias EVENT ausentes; limites/timeout/cancelacion con resultado parcial visible. Las categorias son fixtures del dominio, no una promesa de que todos esos subtipos tengan soporte automatico incorporado.

Comparar cuatro configuraciones de **AgentMemory actual**: resolver legado con las protecciones temporales actuales; politica nueva sin LLM; politica nueva con contexto y adjudicacion acotada; claves verificadas del host cuando el caso realmente las proporcione. El modo legado actual no se etiquetara como 1.9 historico. Los mocks probaran combinaciones y errores; no mediran precision de un LLM.

Cada caso se medira por etapas:

1. Extraccion: hechos y citas conservados, rechazados o ausentes.
2. Candidatos/contexto: candidato correcto ofrecido, fuentes disponibles/ofrecidas y truncamiento.
3. Identidad: fusion correcta/incorrecta, separacion correcta, pendiente justificado o pendiente por carencia de contexto.
4. Escritura real: decisiones, observaciones, bindings, procedencia y aislamiento; lectura tras nueva sesion/instancia.
5. Recuperacion: IDs de hechos esperados y excluidos, ventana temporal/upcoming y detalles sin fecha. Recorrido nativo sin vector solo donde exista esa capacidad.
6. Prompt y respuesta: evidencia realmente enviada; fechas, negaciones y conflicto correctos. Recuperar datos no demuestra que el modelo razone bien.

No agregar todo en una unica accuracy: informar falsas fusiones, cobertura/resoluciones pendientes, recall/precision de hechos, cobertura de enlaces, citas validas, coste y llamadas por familia/configuracion. Una abstencion correcta no equivale a una fusion erronea, pero una abstencion sistematica tampoco es buen recall.

Comenzar con reproducciones deterministas y Neo4j desechable. Despues usar pocos modelos reales representativos, incluidos Bariloche original y un evento ambiguo/remoto como controles, dentro del presupuesto acumulado autorizado. No ejecutar todos los cruces con LLM ni instalar competidores para convertir esta correccion en un gran benchmark.

## 6. Orden de correccion y criterio de cierre

1. Reproducir la ausencia de fuentes y los descartes del writer; conservar el baseline que falla.
2. Proveer contexto historico acotado y autorizado a una decision elegible, conservando las fuentes originales de cada observacion. Revalidar fuentes y candidatos al persistir, sin consultas/modelo adicionales apagado o sin presupuesto.
3. Fortalecer forma de operaciones, referencias y citas del writer; correccion limitada y resultado parcial explicito. No inventar entidades ni igualdad para completar la estructura.
4. Probar escritura secuencial y recuperacion real, con casos negativos, presupuestos y compatibilidad afectados.
5. Repetir Bariloche sin modificar sus entradas para encajar la implementacion. Revisar por separado storage, decisiones, vinculos, prompt y respuesta. Cerrar solo despues de corregir y repetir los disparadores relevantes.

La fuente nueva de seis reproducciones de auditoria esta preparada fuera de la suite publica. El primer intento compilo, pero el monitor aborto antes de obtener resultados por RAM disponible cercana a la reserva. **No se afirma que esas seis pruebas se hayan ejecutado ni que una correccion este aprobada.** No hubo nuevas llamadas de pago ni cambios de produccion en esta revision.
