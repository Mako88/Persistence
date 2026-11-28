-- Close PR B (close-build-plan.md B3): one ADD COLUMN per migration, for the runner reason in 014.

-- When the fact stopped standing: set by approving an end draft (B3); null while it stands.

ALTER TABLE Facts ADD COLUMN EndedUtc TEXT NULL;
