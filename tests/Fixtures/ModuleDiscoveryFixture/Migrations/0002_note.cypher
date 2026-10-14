MATCH (n:ReviewItem) WHERE n.note IS NULL SET n.note = 'https://example.org/a;b';
