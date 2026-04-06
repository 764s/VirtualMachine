namespace KOF98
{
    /// <summary>
    /// Collision detection and resolution system.
    /// Handles hit detection (hitbox vs hurtbox), block detection, and push resolution.
    /// </summary>
    public class CollisionSystem
    {
        /// <summary>
        /// Check if a character's attack group hits any opponent's hurtbox.
        /// Returns the first hit target ID, or -1 if no hit.
        /// </summary>
        public int CheckAttackHit(CharacterManager chars, int attackerId, int groupId)
        {
            var attacker = chars.Get(attackerId);
            if (attacker == null) return -1;

            // Find the hitbox for the given group
            FRect hitbox = FRect.Empty;
            for (int i = 0; i < attacker.HitBoxCount; i++)
            {
                if (attacker.HitBoxes[i].GroupId == groupId)
                {
                    hitbox = attacker.HitBoxes[i].Box;
                    break;
                }
            }
            if (hitbox.IsEmpty) return -1;

            int facingSign = attacker.FacingSign;
            hitbox.GetWorldBounds(attacker.Body.Position, facingSign,
                out float hMinX, out float hMinY, out float hMaxX, out float hMaxY);

            // Check against all opponents' hurtboxes
            for (int i = 0; i < chars.Count; i++)
            {
                var target = chars.Get(i);
                if (target == null || target.Id == attackerId
                    || target.Team == attacker.Team || !target.IsAlive)
                    continue;

                int targetFacing = target.FacingSign;
                for (int j = 0; j < target.HurtBoxCount; j++)
                {
                    target.HurtBoxes[j].GetWorldBounds(target.Body.Position, targetFacing,
                        out float tMinX, out float tMinY, out float tMaxX, out float tMaxY);

                    if (FRect.Overlaps(hMinX, hMinY, hMaxX, hMaxY, tMinX, tMinY, tMaxX, tMaxY))
                        return target.Id;
                }
            }
            return -1;
        }

        /// <summary>
        /// Check if a character's attack group is blocked by any opponent.
        /// Returns the blocking target ID, or -1 if not blocked.
        /// </summary>
        public int CheckAttackBlocked(CharacterManager chars, int attackerId, int groupId)
        {
            var attacker = chars.Get(attackerId);
            if (attacker == null) return -1;

            FRect hitbox = FRect.Empty;
            for (int i = 0; i < attacker.HitBoxCount; i++)
            {
                if (attacker.HitBoxes[i].GroupId == groupId)
                {
                    hitbox = attacker.HitBoxes[i].Box;
                    break;
                }
            }
            if (hitbox.IsEmpty) return -1;

            int facingSign = attacker.FacingSign;
            hitbox.GetWorldBounds(attacker.Body.Position, facingSign,
                out float hMinX, out float hMinY, out float hMaxX, out float hMaxY);

            for (int i = 0; i < chars.Count; i++)
            {
                var target = chars.Get(i);
                if (target == null || target.Id == attackerId
                    || target.Team == attacker.Team || !target.IsAlive)
                    continue;

                // Target must be in block state and have a block box
                if (!target.HasTag(GameConstants.TAG_BLOCK)) continue;
                if (target.BlockBox.IsEmpty) continue;

                int targetFacing = target.FacingSign;
                target.BlockBox.GetWorldBounds(target.Body.Position, targetFacing,
                    out float tMinX, out float tMinY, out float tMaxX, out float tMaxY);

                if (FRect.Overlaps(hMinX, hMinY, hMaxX, hMaxY, tMinX, tMinY, tMaxX, tMaxY))
                    return target.Id;
            }
            return -1;
        }

        /// <summary>
        /// Resolve pushbox overlaps between characters.
        /// Prevents characters from overlapping by pushing them apart equally.
        /// </summary>
        public void ResolvePushBoxes(CharacterManager chars)
        {
            for (int i = 0; i < chars.Count; i++)
            {
                var a = chars.Get(i);
                if (a == null || !a.IsAlive) continue;

                for (int j = i + 1; j < chars.Count; j++)
                {
                    var b = chars.Get(j);
                    if (b == null || !b.IsAlive) continue;

                    ResolvePushPair(a, b);
                }
            }
        }

        private void ResolvePushPair(Character a, Character b)
        {
            a.PushBox.GetWorldBounds(a.Body.Position, a.FacingSign,
                out float aMinX, out float aMinY, out float aMaxX, out float aMaxY);
            b.PushBox.GetWorldBounds(b.Body.Position, b.FacingSign,
                out float bMinX, out float bMinY, out float bMaxX, out float bMaxY);

            if (!FRect.Overlaps(aMinX, aMinY, aMaxX, aMaxY, bMinX, bMinY, bMaxX, bMaxY))
                return;

            // Calculate horizontal overlap and push apart
            float overlapLeft = aMaxX - bMinX;
            float overlapRight = bMaxX - aMinX;
            float push = (overlapLeft < overlapRight ? overlapLeft : overlapRight) * 0.5f;

            if (a.Body.Position.X < b.Body.Position.X)
            {
                a.Body.Position = new FVec2(a.Body.Position.X - push, a.Body.Position.Y);
                b.Body.Position = new FVec2(b.Body.Position.X + push, b.Body.Position.Y);
            }
            else
            {
                a.Body.Position = new FVec2(a.Body.Position.X + push, a.Body.Position.Y);
                b.Body.Position = new FVec2(b.Body.Position.X - push, b.Body.Position.Y);
            }

            // Re-clamp to stage bounds
            a.Body.ClampToStage();
            b.Body.ClampToStage();
        }
    }
}
