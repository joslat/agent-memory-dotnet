CREATE INDEX cli_review_note IF NOT EXISTS FOR (n:CliReviewItem) ON (n.owner_id, n.note);
MATCH (n:CliReviewItem) SET n.count = n.count + 1;
