CREATE CONSTRAINT pack_local_gate_id IF NOT EXISTS FOR (g:PackLocalGate) REQUIRE g.id IS UNIQUE;
